# Building a proprietary SWP slave on this model

This repo's `SWPSlave` is meant to be **subclassed**, not copied. What you
inherit is the part that is tedious to get right and easy to get subtly wrong:

- the ETSI TS 102 613 state machine — `DEACTIVATED` / `SUSPENDED` / `ACTIVATED`
  and the four transitions between them;
- the deadline discipline — answering an ACTIVATE inside **P6 and P7**, a RESUME
  inside **P3max**, and the fact that the link reaches `ACTIVATED` at the end of
  the master's **P2** idle bits whether you have answered yet or not;
- the queue-and-resume paths in both directions, so data offered on a suspended
  link wakes it rather than being dropped;
- byte accounting, traffic recording, the Monitor surface, and an optional TCP
  socket for the slave end.

What you write is the part that is actually yours: what your part *exchanges*
during activation, and what it does with received bytes.

---

## 1. The rule that will bite you first

**Override. Never hide.**

`SWPController` holds the slave as an `ISWPSlave` and calls it through the
interface:

```csharp
var slave = RegisteredPeripheral;   // typed ISWPSlave
slave.OnActivate();
```

So this compiles, reads correctly, and **never runs**:

```csharp
public class AcmeSlave : SWPSlave
{
    public new void OnActivate() { ... }     // WRONG — silently dead
}
```

C# resolves the interface call to the base implementation. There is no warning
at the call site and no error at runtime; you simply get default behaviour.
Always write `override`:

```csharp
public override void OnActivate() { ... }    // right
```

Every method the controller can call is `virtual` for exactly this reason. The
suite has a regression test for it — `Subclass Overrides Are Called Through The
Controller`, paired with `Base Slave Does Not Echo` so it is measuring the
override rather than something the base was doing anyway.

---

## 2. The seams

### Virtual — override any of these

| Member | When it runs | Typical reason to override |
|---|---|---|
| `OnActivate()` | master addressed you, at its `t0 + P5` | change *when* you answer; usually you want `AnswerActivation` instead |
| `AnswerActivation()` | `ActivationResponseTimeMicroseconds` later | **your activation handshake** — ACT_SYNC, ACT_POWER_MODE, bit-rate negotiation |
| `OnResume()` | end of the master's P2 idle bits | rarely |
| `AnswerResume()` | `ResumeResponseTimeMicroseconds` later | your transition-sequence answer |
| `OnSuspend()` | master stopped the bit clock | park state, stop your own timers |
| `OnDeactivate()` | master held SWIO low past P4 | drop context, clear keys |
| `ReceiveFromMaster(byte[])` | a burst arrived on S1 | **your protocol stack** |
| `SendToMaster(byte[])` | you are sending on S2 | rarely; the base handles the suspended-link case |
| `SetState(state, transition)` | you mirror an observed state | rarely |
| `Reset()` | machine reset | clear your own state — **call `base.Reset()`** |
| `Dispose()` | teardown | release your resources — **call `base.Dispose()`** |

### Protected — the things you build with

| Member | What it gives you |
|---|---|
| `Master` | the `ISWPMaster` to answer on: `NotifySlaveActivated()`, `NotifySlaveResumed()`, `ReceiveFromSlave()`, `RequestResume()`. Null before the first ACTIVATE. |
| `Timer` | schedule on **virtual time**: `Timer.Schedule(TimeInterval, Action)`. `Timer.CancelAll()` invalidates everything pending. |
| `SetState(...)` | move this end's view of the link and raise `StateChanged` |
| `FlushPendingToMaster()` | drain what queued while the link was down |
| `Machine` | the `IMachine`, for `HandleTimeDomainEvent` when data arrives off-thread |

### Public — configuration your `.repl` can set

`P1`..`P7` (microseconds), `ActivationResponseTimeMicroseconds`,
`ResumeResponseTimeMicroseconds`, `RecordTraffic`, `MaxRecordedBursts`, `port`.

---

## 3. The smallest real slave

```csharp
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.SWP;

namespace Acme.Peripherals
{
    public class AcmeSecureElement : SWPSlave
    {
        public AcmeSecureElement(IMachine machine, int port = 0, bool autoStart = true)
            : base(machine, port, autoStart)
        {
        }

        public override void ReceiveFromMaster(byte[] data)
        {
            // Chain first: the base keeps the byte counters, the traffic record
            // and the DataReceived event working.
            base.ReceiveFromMaster(data);

            foreach(var b in data)
            {
                stack.Feed(b);          // your framing, CRC, HCI, applet dispatch
            }
        }

        private void OnStackResponse(byte[] response)
        {
            SendToMaster(response);     // base handles a suspended link for you
        }

        private readonly AcmeProtocolStack stack = new AcmeProtocolStack();
    }
}
```

In the platform description:

```repl
swp:  SWP.SWPController @ sysbus { port: 3460 }
uicc: Acme.AcmeSecureElement @ swp
    P3: 500
    P6: 10000
    P7: 20000
```

That is the whole integration. The controller does not know or care that `uicc`
is not a stock `SWPSlave`.

---

## 4. A proprietary activation handshake

This is the override most real parts need. `AnswerActivation()` is called at the
moment your part would answer; whatever you do, you must reach
`Master.NotifySlaveActivated()` inside the master's **P6 and P7** or the master
fails the activation — which is correct, and is what you want to be able to test.

```csharp
protected override void AnswerActivation()
{
    // Your part does not simply become ready: it exchanges ACT_SYNC, then
    // negotiates a power mode, then declares itself ready.
    SendToMaster(BuildActSync());

    Timer.Schedule(TimeInterval.FromMicroseconds(SyncToPowerModeUs), () =>
    {
        SendToMaster(BuildActPowerMode(currentPowerMode));

        Timer.Schedule(TimeInterval.FromMicroseconds(PowerModeToReadyUs), () =>
        {
            SetState(SWPState.Activated, SWPTransition.Activate);
            Master?.NotifySlaveActivated();
            FlushPendingToMaster();          // release anything queued
        });
    });
}
```

Three rules for any override that schedules:

1. **Never block.** These run on the emulation thread. Blocking freezes virtual
   time — timers stop, interrupts never fire, and the simulation looks hung with
   no log output. Use `Timer.Schedule`.
2. **Call `SetState(...)` before `NotifySlaveActivated()`** so the two ends do
   not disagree for an instant.
3. **Call `FlushPendingToMaster()`** if you replace `AnswerActivation` or
   `AnswerResume` wholesale — the base does it for you, an override does not.

To test the failure path, set `ActivationResponseTimeMicroseconds` past `P6` and
assert the master gives up. `tests/swp.robot` does this in
`Activation Fails When The Slave Misses P6`.

---

## 5. Data arriving from outside the emulation

If your slave is fed by a socket, a file, or another host process, **do not**
touch link state from that thread. Copy the buffer and hand it over:

```csharp
private void OnHostData(byte[] data)        // socket reader thread
{
    var copy = new byte[data.Length];       // the provider reuses its buffer
    Array.Copy(data, copy, data.Length);
    Machine.HandleTimeDomainEvent<byte[]>(SendToMaster, copy, false);
}
```

`SWPSlave.StartSocket()` already does this if you just set `port`.

---

## 6. If you cannot derive from `SWPSlave`

Your class may already have a base class it must keep. Implement `ISWPSlave`
instead — the controller only ever talks to the interface:

```csharp
public interface ISWPSlave : ISWPEndpoint
{
    void AttachMaster(ISWPMaster master);
    void OnActivate();
    void OnDeactivate();
    void OnSuspend();
    void OnResume();
    void ReceiveFromMaster(byte[] data);
    void RequestResumeFromMaster();
}

public interface ISWPEndpoint : IPeripheral
{
    SWPState State { get; }
    event Action<byte[]> DataReceived;
    event Action<SWPStateChangedEventArgs> StateChanged;
    void TransmitToPeer(byte[] data);
}
```

`ISWPEndpoint` is deliberately the full surface anything above the link needs,
so **`SWPRegisterInterface` still works** — it binds to the interfaces, never to
the shipped classes. You keep the memory-mapped `CR`/`SR`/`TDR`/`RDR` block for
your firmware whichever route you take. `tests/swp.robot` checks this in
`A From-Scratch Endpoint Still Drives The Register Block`.

The cost is that you reimplement the P3/P6/P7 discipline yourself. Prefer
subclassing unless you genuinely cannot.

---

## 7. Loading your peripheral

Two ways, both supported, no code change between them.

**Runtime compilation** (fastest loop — no Renode rebuild):

```
include @peripherals/SWP.cs
include @peripherals/SWPController.cs
include @peripherals/SWPSlave.cs
include @acme/AcmeSecureElement.cs      # yours, after its base
```

Order matters: the Monitor compiles each file into its **own assembly**, so a
file may only reference types from files already included. That also means
anything shared across files must be `public` — `internal` will not resolve.
Cross-file subclassing works; `SWPLoopbackSlave` is loaded this way in the test
suite precisely to prove it.

**Compiled into the Renode tree**: drop the files under
`src/Infrastructure/src/Emulator/Peripherals/Peripherals/SWP/` and rebuild.
Same sources, no edits.

---

## 8. Checklist before you trust your slave

- [ ] Every method the controller calls is `override`, not `new`
- [ ] `base.Reset()` and `base.Dispose()` are chained
- [ ] Nothing in an override blocks; delays go through `Timer.Schedule`
- [ ] `AnswerActivation` reaches `NotifySlaveActivated()` inside P6 **and** P7
- [ ] `AnswerResume` reaches `NotifySlaveResumed()` inside P3max
- [ ] Any override of those two calls `FlushPendingToMaster()`
- [ ] Off-thread data goes through `Machine.HandleTimeDomainEvent`
- [ ] A test asserts the link comes up, and one asserts it **fails** when your
      slave answers too late — a slave that can never fail activation is a slave
      whose timing you are not really testing
- [ ] Robot tests advance time with `emulation RunFor`, never a host sleep

---

## 9. Where to look in this repo

| File | Why |
|---|---|
| `peripherals/SWPLoopbackSlave.cs` | the smallest real subclass; start by copying it |
| `peripherals/SWPSlave.cs` | the seams, each documented at its definition |
| `peripherals/SWP.cs` | the interfaces and `SWPTimings` |
| `tests/swp.robot` | how to test a slave, including the failure paths |
| `firmware/src/main.c` | the other end of the wire: framing above an opaque link |
| `README.md` | the flows as Mermaid diagrams |

One thing worth repeating from the README: the **numeric defaults for P5–P7 are
engineering defaults, not quoted ETSI figures** (the normative table is
paywalled). The semantics of P1–P4 follow TS 102 613 clause 8. Set the
properties from your copy of the spec before you use this to qualify anything.
