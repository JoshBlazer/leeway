using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Leeway.Core;

namespace Leeway.Simulation;

/// <summary>
/// Runs a schedule of device actions and faults against the real protocol logic, with a modelled transport.
/// No threads, wall clock, or ambient randomness: a run is a pure function of its schedule. See docs/design/simulation.md.
/// </summary>
public static class Simulator
{
    private const string EntityId = "e1";
    private const string CounterField = "c";
    private const string RegisterField = "r";
    private const string SetField = "s";
    private const string Resource = "e1/capacity";

    public static SimResult Run(Schedule schedule, ProtocolParts? parts = null) => new Execution(schedule, parts ?? new ProtocolParts()).Execute();

    private sealed class Device(string name, Func<long> wall)
    {
        public string Name { get; } = name;
        public MergeDocument Pulled { get; set; } = MergeDocument.Empty;
        public OperationLog Log { get; } = new();

        /// <summary>Acknowledged deltas the pulled document may not include yet, with the row stamp that does include them.</summary>
        public List<(MergeDocument Delta, long Stamp)> Settling { get; } = [];
        public Wallet Wallet { get; set; } = new(0, 0, 0);
        public HybridClock Clock { get; } = new(name, wall);
        public HybridTimestamp Observed { get; set; }
        public long Sequence { get; set; }
        public long Skew { get; set; }
        public bool Partitioned { get; set; }
        public long EntityToken { get; set; }
        public long LeaseToken { get; set; }
    }

    private sealed class Execution
    {
        private readonly Schedule schedule;
        private readonly ProtocolParts parts;
        private readonly List<Device> devices = [];
        private readonly List<string> violations = [];
        private readonly IAllocationPolicy policy = new FixedLease(8);

        // Server state.
        private MergeDocument serverDoc = MergeDocument.Empty;
        private long entityStamp;
        private EscrowLedger ledger;
        private StampAuthority authority = StampAuthority.Create();
        private long lastRowStamp;
        private readonly Dictionary<string, long> leaseStamps = [];
        private readonly Dictionary<string, OperationOutcome> applied = [];
        private readonly Dictionary<string, int> applyCounts = [];
        private readonly Dictionary<string, long> appliedStamps = [];

        // Virtual time. It moves only on Advance, so same-millisecond stamps are the norm (C19).
        private long now = 1_000_000;

        // The oracle: what really happened, kept independently of the protocol.
        private long issuedCounterSum;
        private readonly HashSet<string> supersededValues = [];
        private readonly Dictionary<string, string> addedTags = [];
        private readonly HashSet<string> removedTags = [];
        private bool wroteOff;
        private int opsIssued;
        private SimStats stats = new();
        private readonly IncrementalHash trace = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public Execution(Schedule schedule, ProtocolParts parts)
        {
            this.schedule = schedule;
            this.parts = parts;
            this.ledger = EscrowLedger.Create(Resource, schedule.Total);
            for (int i = 0; i < schedule.Devices; i++)
            {
                int index = i;
                this.devices.Add(new Device($"d{i}", () => this.now + this.devices[index].Skew));
            }
        }

        public SimResult Execute()
        {
            for (int i = 0; i < this.schedule.Events.Length && this.violations.Count == 0; i++)
            {
                Step(this.schedule.Events[i], i);
                CheckAfterEveryEvent(i);
                Trace($"{i}|{this.now}|{this.lastRowStamp}|{this.applied.Count}|{this.ledger.Unleased}");
            }

            if (this.violations.Count == 0)
            {
                HealAndSettle();
                CheckAfterHealing();
            }

            Trace(this.serverDoc.ToJson() + this.ledger.ToJson() + string.Join(";", this.violations));
            return new SimResult([.. this.violations], Convert.ToHexString(this.trace.GetHashAndReset())[..16], this.stats);
        }

        private void Trace(string text) => this.trace.AppendData(Encoding.UTF8.GetBytes(text));

        private void Step(SimEvent e, int index)
        {
            Device device = this.devices[Math.Abs(e.Device) % this.devices.Count];
            switch (e.Type)
            {
                case EventType.CounterAdd: CounterAdd(device, e.Value); break;
                case EventType.RegisterWrite: RegisterWrite(device, index); break;
                case EventType.SetAdd: SetAdd(device, e.Value); break;
                case EventType.SetRemove: SetRemove(device, e.Value); break;
                case EventType.Spend: Spend(device, e.Value); break;
                case EventType.ReturnLease: Return(device, e.Value); break;
                case EventType.RequestLease: Issue(device, OperationKind.LeaseRequest, null, e.Value); break;
                case EventType.Push: Push(device, e.Value, e.Fault); break;
                case EventType.Pull: Pull(device, e.Fault); break;
                case EventType.Partition: device.Partitioned = true; break;
                case EventType.Heal: device.Partitioned = false; break;
                case EventType.Crash: Crash(device); break;
                case EventType.Skew: device.Skew += e.Value; break;
                case EventType.Advance: this.now += Math.Abs(e.Value); break;
                case EventType.WriteOff: WriteOff(device); break;
                default: throw new InvalidOperationException($"Unknown event {e.Type}.");
            }
        }

        // ---------- Device actions ----------

        private MergeDocument ViewOf(Device device)
        {
            MergeDocument view = device.Pulled;
            if (this.parts.ViewIncludesPending)
            {
                // Without the acknowledged-but-not-yet-pulled deltas, the next counter add would compute its
                // cumulative total from a stale base, and max would absorb it. The simulator found this.
                foreach ((MergeDocument delta, _) in device.Settling)
                {
                    view = this.parts.ViewMerge(view, delta);
                }

                foreach (Operation op in device.Log.Pending)
                {
                    if (op.Delta is not null)
                    {
                        view = this.parts.ViewMerge(view, op.Delta);
                    }
                }
            }

            return view;
        }

        private Operation Issue(Device device, OperationKind kind, Func<HybridTimestamp, string, MergeDocument>? delta = null, long amount = 0)
        {
            long sequence = ++device.Sequence;
            HybridTimestamp stamp = this.parts.DeviceStamp(device.Clock, this.now + device.Skew, device.Name);
            string id = $"{device.Name}-{sequence}";
            IReadOnlyList<Operation> pending = device.Log.Pending;
            long settledBelow = pending.Count > 0 ? pending[0].Sequence : sequence;
            Operation op = new(
                id, device.Name, sequence, stamp, device.Observed, kind, "Zone", EntityId,
                delta?.Invoke(stamp, id), kind == OperationKind.Merge ? null : Resource, amount, settledBelow);
            device.Log.Append(op);
            this.opsIssued++;
            return op;
        }

        private void CounterAdd(Device device, long amount)
        {
            if (amount == 0)
            {
                return;
            }

            this.issuedCounterSum += amount;
            PnCounter current = ViewOf(device).Counter(CounterField);
            Issue(device, OperationKind.Merge, (_, _) => MergeDocument.Empty.WithCounter(CounterField, current.AddDelta(device.Name, amount)));
        }

        private void RegisterWrite(Device device, int index)
        {
            if (ViewOf(device).Registers.TryGetValue(RegisterField, out LwwRegister? seen) && seen.Value is not null)
            {
                this.supersededValues.Add(seen.Value);
            }

            string value = $"w{index}";
            Issue(device, OperationKind.Merge, (stamp, _) => MergeDocument.Empty.WithRegister(RegisterField, new LwwRegister(value, stamp)));
            this.stats = this.stats with { RegistersWritten = this.stats.RegistersWritten + 1 };
        }

        private void SetAdd(Device device, long value)
        {
            string element = "x" + (Math.Abs(value) % 3);
            Issue(device, OperationKind.Merge, (_, id) =>
            {
                this.addedTags[id] = element;
                return MergeDocument.Empty.WithSet(SetField, OrSet.Empty.AddDelta(element, id));
            });
        }

        private void SetRemove(Device device, long value)
        {
            OrSet delta = ViewOf(device).Set(SetField).RemoveDelta("x" + (Math.Abs(value) % 3));
            if (delta.Tombstones.IsEmpty)
            {
                return;
            }

            this.removedTags.UnionWith(delta.Tombstones);
            Issue(device, OperationKind.Merge, (_, _) => MergeDocument.Empty.WithSet(SetField, delta));
        }

        private void Spend(Device device, long amount)
        {
            if (this.parts.WalletSpend(device.Wallet, amount) is { } next)
            {
                device.Wallet = next;
                Issue(device, OperationKind.Spend, null, amount);
                this.stats = this.stats with { SpendsConfirmed = this.stats.SpendsConfirmed + 1 };
            }
            else
            {
                Issue(device, OperationKind.ProvisionalSpend, null, amount);
            }
        }

        private void Return(Device device, long amount)
        {
            (Wallet next, long returned) = this.parts.WalletReturn(device.Wallet, amount);
            device.Wallet = next;
            if (returned > 0)
            {
                Issue(device, OperationKind.ReturnLease, null, returned);
                this.stats = this.stats with { Returns = this.stats.Returns + 1 };
            }
        }

        private void Crash(Device device)
        {
            // Durable state survives. The in-flight batch is modelled by the push faults.
            if (!this.parts.SequenceSurvivesCrash)
            {
                device.Sequence = 0;
            }
        }

        private void WriteOff(Device device)
        {
            this.ledger = this.ledger.WriteOff(device.Name);
            this.wroteOff = true;
            this.stats = this.stats with { WriteOffs = this.stats.WriteOffs + 1 };
        }

        // ---------- Transport ----------

        private void Push(Device device, long shuffle, Fault fault)
        {
            if (device.Partitioned)
            {
                return;
            }

            Random random = new((int)shuffle);
            List<Operation> batch = device.Log.Pending.OrderBy(_ => random.Next()).ToList();
            int faultAt = batch.Count == 0 ? 0 : (int)(Math.Abs(shuffle) % batch.Count);
            List<(Operation Op, OperationOutcome Outcome, long Stamp)> acknowledged = [];

            for (int i = 0; i < batch.Count; i++)
            {
                Operation op = batch[i];
                Fault here = i == faultAt ? fault : Fault.None;
                switch (here)
                {
                    case Fault.RequestLost:
                        Acknowledge(device, acknowledged);
                        return;
                    case Fault.ResponseLost:
                        Deliver(op);
                        Acknowledge(device, acknowledged);
                        return;
                    case Fault.MidBatchDrop:
                        // C13: everything so far reached the server, and the device recorded none of it.
                        Deliver(op);
                        return;
                    case Fault.Timeout:
                    case Fault.TimeoutAfterApply:
                        if (here == Fault.TimeoutAfterApply)
                        {
                            Deliver(op);
                        }

                        // C24: the push reports success. Only a client that trusts that loses the operation.
                        if (this.parts.TrustsPushResult)
                        {
                            device.Log.Forget(op.Id);
                        }

                        break;
                    case Fault.Duplicate:
                        Deliver(op);
                        acknowledged.Add(Ack(op, Deliver(op)));
                        break;
                    default:
                        acknowledged.Add(Ack(op, Deliver(op)));
                        break;
                }
            }

            Acknowledge(device, acknowledged);
        }

        private (Operation Op, OperationOutcome Outcome, long Stamp) Ack(Operation op, OperationOutcome outcome)
            => (op, outcome, this.appliedStamps.GetValueOrDefault(op.Id));

        private static void Acknowledge(Device device, List<(Operation Op, OperationOutcome Outcome, long Stamp)> acknowledged)
        {
            foreach ((Operation op, OperationOutcome outcome, long stamp) in acknowledged)
            {
                device.Log.Acknowledge(op.Id, outcome);
                if (op.Delta is not null && stamp > device.EntityToken)
                {
                    device.Settling.Add((op.Delta, stamp));
                }
            }
        }

        private OperationOutcome Deliver(Operation op)
        {
            if (this.parts.ServerDedupes && this.applied.TryGetValue(op.Id, out OperationOutcome? stored))
            {
                this.stats = this.stats with { Replays = this.stats.Replays + 1 };
                return stored;
            }

            this.applyCounts[op.Id] = this.applyCounts.GetValueOrDefault(op.Id) + 1;
            OperationOutcome outcome;
            if (op.Kind == OperationKind.Merge)
            {
                (HybridTimestamp canonical, this.authority) = this.parts.Admit(this.authority, op, this.now);
                if (canonical.Physical < op.Stamp.Physical)
                {
                    this.stats = this.stats with { DriftCorrections = this.stats.DriftCorrections + 1 };
                }

                this.serverDoc = this.parts.ServerMerge(this.serverDoc, op.Delta!.WithRegisterStamps(canonical));
                this.entityStamp = NextStamp();
                this.appliedStamps[op.Id] = this.entityStamp;
                outcome = new OperationOutcome(OutcomeStatus.Merged);
            }
            else
            {
                (this.ledger, outcome) = this.parts.ApplyEscrow(this.ledger, op, this.policy);
                Count(outcome);
                if (outcome.Status == OutcomeStatus.LeaseGranted && outcome.Amount > 0)
                {
                    this.leaseStamps[op.Device] = NextStamp();
                }
            }

            this.applied[op.Id] = outcome;
            return outcome;
        }

        private void Count(OperationOutcome outcome)
        {
            this.stats = outcome.Status switch
            {
                OutcomeStatus.ProvisionalAccepted => this.stats with { ProvisionalAccepted = this.stats.ProvisionalAccepted + 1 },
                OutcomeStatus.ProvisionalRejected => this.stats with { ProvisionalRejected = this.stats.ProvisionalRejected + 1 },
                OutcomeStatus.LeaseGranted when outcome.Amount > 0 => this.stats with { LeasesGranted = this.stats.LeasesGranted + 1 },
                OutcomeStatus.Overdraft => this.stats with { Overdrafts = this.stats.Overdrafts + 1 },
                _ => this.stats,
            };
        }

        private long NextStamp()
        {
            this.lastRowStamp = this.parts.NextRowStamp(this.lastRowStamp, this.now);
            return this.lastRowStamp;
        }

        /// <summary>Like Datasync's pull: only rows with a stamp strictly after the token are delivered (C19).</summary>
        private void Pull(Device device, Fault fault)
        {
            if (device.Partitioned || fault is Fault.ResponseLost or Fault.RequestLost or Fault.Timeout or Fault.MidBatchDrop)
            {
                return;
            }

            if (this.entityStamp > device.EntityToken)
            {
                device.Pulled = this.serverDoc;
                device.EntityToken = this.entityStamp;
                device.Settling.RemoveAll(s => s.Stamp <= device.EntityToken);
                HybridTimestamp highest = this.serverDoc.HighestStamp();
                if (highest.CompareTo(device.Observed) > 0)
                {
                    device.Observed = highest;
                }

                device.Clock.Observe(highest);
            }

            long leaseStamp = this.leaseStamps.GetValueOrDefault(device.Name);
            if (leaseStamp > device.LeaseToken)
            {
                device.Wallet = device.Wallet with { Granted = this.ledger.Accounts[device.Name].Granted };
                device.LeaseToken = leaseStamp;
            }
        }

        private void HealAndSettle()
        {
            foreach (Device device in this.devices)
            {
                device.Partitioned = false;
            }

            for (int round = 0; round < 3; round++)
            {
                this.now++;
                foreach (Device device in this.devices)
                {
                    Push(device, round, Fault.None);
                }

                foreach (Device device in this.devices)
                {
                    Pull(device, Fault.None);
                }
            }
        }

        // ---------- Properties ----------

        private void Violation(string text) => this.violations.Add(text);

        private void CheckAfterEveryEvent(int index)
        {
            // Property 1: confirmed state never violates an invariant.
            if (this.ledger.Consumed > this.ledger.Total)
            {
                Violation($"P1 at event {index}: consumed {this.ledger.Consumed} exceeds total {this.ledger.Total}.");
            }

            if (this.ledger.Unleased < 0)
            {
                Violation($"P1 at event {index}: unleased is {this.ledger.Unleased}.");
            }

            foreach ((string name, EscrowAccount account) in this.ledger.Accounts)
            {
                if (account.Held < 0)
                {
                    Violation($"P1 at event {index}: {name} holds {account.Held}.");
                }
            }

            foreach (Device device in this.devices)
            {
                if (device.Wallet.Available < 0)
                {
                    Violation($"P1 at event {index}: {device.Name} wallet is at {device.Wallet.Available}.");
                }
            }

            if (!this.wroteOff)
            {
                long overdraft = this.ledger.Accounts.Values.Sum(a => a.Overdraft);
                if (overdraft > 0)
                {
                    Violation($"P1 at event {index}: overdraft of {overdraft} with no write-off.");
                }

                long spent = this.devices.Sum(d => d.Wallet.Spent);
                if (spent > this.ledger.Total)
                {
                    Violation($"P1 at event {index}: devices confirmed {spent} of {this.ledger.Total}.");
                }
            }

            // Property 4: conservation.
            if (!this.ledger.IsConserved)
            {
                Violation($"P4 at event {index}: quantity is not conserved.");
            }

            // Property 8: a wrong clock has bounded influence. Each causally later write may add 1 ms.
            if (this.serverDoc.Registers.TryGetValue(RegisterField, out LwwRegister? register)
                && register.Stamp.Physical > this.now + this.authority.MaxDriftMs + this.opsIssued)
            {
                Violation($"P8 at event {index}: stamp {register.Stamp.Physical} is beyond the drift limit at {this.now}.");
            }
        }

        private void CheckAfterHealing()
        {
            foreach (Device device in this.devices)
            {
                // Property 2: convergence.
                if (device.Log.Pending.Count > 0)
                {
                    Violation($"P2: {device.Name} still has {device.Log.Pending.Count} pending operations.");
                }

                if (!device.Pulled.Equals(this.serverDoc))
                {
                    Violation($"P2: {device.Name} did not converge.");
                }

                long granted = this.ledger.Accounts.GetValueOrDefault(device.Name)?.Granted ?? 0;
                if (device.Wallet.Granted != granted)
                {
                    Violation($"P2: {device.Name} believes it was granted {device.Wallet.Granted}, not {granted}.");
                }

                // Property 7: accounts agree.
                EscrowAccount account = this.ledger.Accounts.GetValueOrDefault(device.Name) ?? new EscrowAccount();
                if (device.Wallet.Spent != account.Spent + account.Overdraft)
                {
                    Violation($"P7: {device.Name} confirmed {device.Wallet.Spent}, but the server has {account.Spent} plus {account.Overdraft} overdraft.");
                }

                if (!this.wroteOff && device.Wallet.Returned != account.Returned)
                {
                    Violation($"P7: {device.Name} returned {device.Wallet.Returned}, but the server has {account.Returned}.");
                }
            }

            // Property 3: no operation applied twice.
            foreach ((string id, int count) in this.applyCounts)
            {
                if (count > 1)
                {
                    Violation($"P3: {id} was applied {count} times.");
                }
            }

            // Property 5: no lost updates.
            long counter = this.serverDoc.Counter(CounterField).Value;
            if (counter != this.issuedCounterSum)
            {
                Violation($"P5: the counter is {counter}, but {this.issuedCounterSum} was issued.");
            }

            string[] expected = this.addedTags.Where(t => !this.removedTags.Contains(t.Key)).Select(t => t.Value).Distinct().Order(StringComparer.Ordinal).ToArray();
            string[] actual = this.serverDoc.Set(SetField).Elements.Order(StringComparer.Ordinal).ToArray();
            if (!expected.SequenceEqual(actual))
            {
                Violation($"P5: the set is [{string.Join(",", actual)}], expected [{string.Join(",", expected)}].");
            }

            // Property 6: causality.
            if (this.serverDoc.Registers.TryGetValue(RegisterField, out LwwRegister? register) && register.Value is not null && this.supersededValues.Contains(register.Value))
            {
                Violation($"P6: the register holds {register.Value}, which a later write had seen and replaced.");
            }
        }
    }
}

/// <summary>Turns a seed into a schedule. Only this uses randomness, and only here does the seed matter.</summary>
public static class ScheduleGenerator
{
    public static Schedule Generate(int seed)
    {
        Random random = new(seed);
        int devices = random.Next(2, 6);
        long total = random.Next(4, 60);
        int length = random.Next(15, 90);
        ImmutableArray<SimEvent>.Builder events = ImmutableArray.CreateBuilder<SimEvent>(length + devices);

        // Most runs start with leases, so that escrow paths are reached.
        for (int d = 0; d < devices; d++)
        {
            events.Add(new SimEvent(EventType.RequestLease, d, random.Next(1, 12)));
            events.Add(new SimEvent(EventType.Push, d, random.Next()));
            events.Add(new SimEvent(EventType.Pull, d));
        }

        for (int i = 0; i < length; i++)
        {
            int device = random.Next(devices);
            int roll = random.Next(100);
            events.Add(roll switch
            {
                < 12 => new SimEvent(EventType.CounterAdd, device, random.Next(-5, 9)),
                < 22 => new SimEvent(EventType.RegisterWrite, device),
                < 28 => new SimEvent(EventType.SetAdd, device, random.Next(3)),
                < 33 => new SimEvent(EventType.SetRemove, device, random.Next(3)),
                < 45 => new SimEvent(EventType.Spend, device, random.Next(1, 6)),
                < 49 => new SimEvent(EventType.ReturnLease, device, random.Next(1, 5)),
                < 54 => new SimEvent(EventType.RequestLease, device, random.Next(1, 12)),
                < 72 => new SimEvent(EventType.Push, device, random.Next(), PickFault(random)),
                < 84 => new SimEvent(EventType.Pull, device, 0, random.Next(5) == 0 ? Fault.ResponseLost : Fault.None),
                < 88 => new SimEvent(EventType.Partition, device),
                < 92 => new SimEvent(EventType.Heal, device),
                < 94 => new SimEvent(EventType.Crash, device),
                < 96 => new SimEvent(EventType.Skew, device, PickSkew(random)),
                < 99 => new SimEvent(EventType.Advance, 0, random.Next(0, 3) == 0 ? random.Next(1, 600_000) : random.Next(0, 3)),
                _ => new SimEvent(EventType.WriteOff, device),
            });
        }

        return new Schedule(devices, total, events.ToImmutable());
    }

    private static Fault PickFault(Random random) => random.Next(12) switch
    {
        0 => Fault.RequestLost,
        1 => Fault.ResponseLost,
        2 => Fault.Duplicate,
        3 => Fault.Timeout,
        4 => Fault.TimeoutAfterApply,
        5 => Fault.MidBatchDrop,
        _ => Fault.None,
    };

    private static long PickSkew(Random random) => random.Next(3) switch
    {
        0 => random.Next(-3_000, 3_000),
        1 => random.Next(-600_000, 600_000),
        _ => random.Next(-86_400_000, 86_400_000),
    };
}

/// <summary>Delta debugging: reduces a failing schedule until removing any one event makes the failure go away.</summary>
public static class Shrinker
{
    public static Schedule Shrink(Schedule failing, Func<Schedule, bool> stillFails)
    {
        ImmutableArray<SimEvent> events = failing.Events;
        int chunk = Math.Max(1, events.Length / 2);
        while (true)
        {
            bool removedAny = false;
            for (int start = 0; start < events.Length;)
            {
                ImmutableArray<SimEvent> candidate = events.RemoveRange(start, Math.Min(chunk, events.Length - start));
                if (stillFails(failing with { Events = candidate }))
                {
                    events = candidate;
                    removedAny = true;
                }
                else
                {
                    start += chunk;
                }
            }

            if (chunk == 1 && !removedAny)
            {
                return failing with { Events = events };
            }

            chunk = removedAny ? chunk : Math.Max(1, chunk / 2);
        }
    }
}
