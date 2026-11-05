# Design note: generator and analyzer

Code: `src/Leeway.Generators`, `src/Leeway.Analyzers`, `src/Leeway.Analyzers.CodeFixes`. Tests: `tests/Leeway.Generators.Tests`, `tests/Leeway.Analyzers.Tests`. Follows ADR-0015.

## Generator

For each class with a merge attribute, it emits two things.

**A schema, replacing reflection.** A module initializer registers it, and `MergeSchema.For` prefers a registered schema over reflection:

```csharp
internal static class ZoneLeewaySchema
{
    public static readonly MergeSchema Schema = MergeSchema.Create(typeof(Zone),
    [
        MergeField.Of<Zone, int>("RemainingCapacity", FieldKind.Escrow, static e => e.RemainingCapacity, static (e, v) => e.RemainingCapacity = v, min: 0, lease: 10),
        MergeField.Of<Zone, int>("CheckIns", FieldKind.Counter, static e => e.CheckIns, static (e, v) => e.CheckIns = v),
    ]);

    [ModuleInitializer]
    internal static void Register() => MergeSchema.Register(Schema);
}
```

**For a client entity, a typed API.** A method per field, so that a wrong kind of change no longer compiles. Today it throws at run time:

```csharp
await client.TrySpendRemainingCapacityAsync("north", 1);
await client.AddCheckInsAsync("north", 1);
await client.SetNotesAsync("north", "Turnstile 3 jammed");
await client.AddToFlagsAsync("north", "vip");
```

These are extension methods, so a class need not be `partial`. Several changes as one operation still use `client.EditAsync`.

The server class gets the schema only, since clients do the writing.

**Incremental.** The transform's output is plain strings, which compare by value, so editing an unrelated file does no work. A test checks that.

## Analyzer

| ID | Severity | Rule | Code fix |
|---|---|---|---|
| LEE001 | Warning | A numeric entity field with no merge attribute is decremented. It would fall to whole-row sync, and nothing stops it crossing a limit | Add `[Escrow(Min = 0, Lease = 1)]`, or `[Counter]` |
| LEE002 | Warning | A merged field is assigned directly. On a client the save is refused, and on a server the next projection overwrites it | None |
| LEE003 | Error | A merge attribute on an unsuitable property: `[Counter]` or `[Escrow]` needs `int` or `long`, `[AddWinsSet]` needs `string`, and all need a public getter and setter | None |
| LEE004 | Error | More than one merge attribute on a property | None |
| LEE005 | Warning | A merge attribute on a class that is not an `OfflineEntity` or `LeewayEntity`, where it does nothing | None |
| LEE006 | Warning | `[Escrow]` with `Lease` below 1, so a lease grants nothing | None |

Object initializers are exempt from LEE002, because that is how a server seeds a new entity.

**Code fixes live in their own project,** because analyzers load inside the compiler, where Workspaces may not be available (RS1038).

## Honest limits

- **Not NativeAOT-ready.** Registers still use reflection-based `System.Text.Json`, and Datasync itself may have its own limits. The generator removes reflection from reading attributes, and no more.
- **LEE001 is a heuristic.** A decremented field may be fine, and a field that needs a limit may never be decremented in code the analyzer sees. It only looks at entity classes.
- **LEE002 cannot see through indirection,** such as a write via `dynamic` or reflection. The runtime guard on the client still applies.
- **The client and server classes are not compared.** They are compiled separately, often in separate projects, so mismatched attributes are not caught at build time.
- **Generic classes are skipped,** and nested classes get a schema but no typed API.
