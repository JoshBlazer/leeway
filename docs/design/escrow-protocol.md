# Design note: escrow protocol

Code: `src/Leeway.Core/Escrow.cs`, `Leeway.Datasync.Client`, `Leeway.Datasync.Server`. Tests: `tests/Leeway.Property.Tests/EscrowProperties.cs`, `tests/Leeway.Integration.Tests/Leeway/`.

## Goal

A bounded quantity, such as remaining zone capacity, must never cross its limit, even with every device offline. A device can spend what it holds with no contact at all. This is the **guaranteed offline** class.

## Ledger (server)

```csharp
public sealed record EscrowAccount(long Granted, long Spent, long Returned, long ProvisionalAccepted, long WrittenOff, long Overdraft)
{
    public long Held => Granted - Spent - Returned - WrittenOff;
}

public sealed record EscrowLedger(string Resource, long Total, long Unleased, Accounts)
{
    public long Consumed { get; }                // Sum of Spent and ProvisionalAccepted.
    public long Remaining { get; }               // Total - Consumed.
    public bool IsConserved { get; }             // Total == Unleased + sum(Held) + Consumed.
    public (EscrowLedger, OperationOutcome) RequestLease(string device, long requested, IAllocationPolicy policy);
    public (EscrowLedger, OperationOutcome) Spend(string device, long amount);
    public (EscrowLedger, OperationOutcome) SpendProvisional(string device, long amount);
    public (EscrowLedger, OperationOutcome) Return(string device, long amount);
    public EscrowLedger WriteOff(string device);
}
```

## Wallet (device)

```csharp
public sealed record Wallet(long Granted, long Spent, long Returned)
{
    public long Available => Granted - Spent - Returned;
}
```

**The device's `Spent` and `Returned` are the source of truth for its own actions, and only `Granted` comes from the server.** All three are cumulative. So `Available` needs no snapshot of server state, and it cannot be fooled by a pull that races an acknowledgement.

The rule `Spent + Returned <= Granted` means any subset of a device's reports fits inside its grant, in any order. That is why reordering cannot produce a false overdraft.

## Operations

| Operation | Device | Server | Outcome |
|---|---|---|---|
| Lease request | Nothing | Policy decides, `Unleased` falls, `Granted` rises | `LeaseGranted(amount)` |
| Spend within lease | Confirmed on the spot | `Spent` rises | `SpendConfirmed` |
| Spend beyond lease | **Provisional**, wallet untouched | Funded from `Unleased` if possible | `ProvisionalAccepted` or `ProvisionalRejected` |
| Return | Wallet falls first | `Returned` rises, `Unleased` rises | `LeaseReturned(amount)` |

A provisional spend never touches `Held`. It must not, because the device is counting that lease as its own. It is all or nothing.

## Exactly-once

The op table's primary key is the operation's UUID. A replay is a 409 carrying the stored row, including the original outcome, which `ServerWinsConflictResolver` adopts (C14). Application happens in one transaction with the row insert, so "applied" and "recorded" cannot disagree (S05).

## Local writes must not wait for the network

A queued `SaveChanges` blocks while any push is in flight (C23). So a spend is one local transaction with `addToQueue: false`: an atomic wallet update and an outbox row. Sync copies outbox rows into the queued op table just before pushing. This answers open question 1 from the integration map.

The wallet update is a single conditional `UPDATE`, so concurrent spends cannot overdraw, even from separate contexts.

## Local view

Pull overwrites a row (C17). After a pull, Leeway re-applies unacknowledged deltas and confirmed spends to rows whose version changed. Provisional spends are not applied to the visible value, because they might be rejected, and a rejection changes no row and so could never correct it.

## Allocation

```csharp
public interface IAllocationPolicy { long Decide(EscrowLedger ledger, string device, long requested); }
public sealed record FixedLease(long Size) : IAllocationPolicy;
public sealed record EqualSplit(int ExpectedDevices, long Floor) : IAllocationPolicy;
```

Velocity-based rebalancing is a later layer.

## Honest limits

- **Write-off can break the rule.** An admin writing off a device returns its holding to the pool (ADR-0005). If the device was only offline, its later spends cannot be funded. They are confirmed on the device, so real usage may exceed the limit. The server records this as `Overdraft`, which is reported rather than hidden.
- **Stranded quota.** A lost device holds its lease until written off, so a venue can show capacity nobody can use.
- **Provisional means provisional.** A person at a gate told "provisional" may later be rejected.
- **No authentication.** `Device` is whatever the client claims. An app must authenticate devices, otherwise a client can spend from another's lease.
- **A malicious or modified client is out of scope.** The guarantee assumes the wallet code runs as written. A dishonest device shows up as an overdraft.
- **SQLite with one server process.** The write gate is in-process, and only this setup is tested (ADR-0003, ADR-0013).
