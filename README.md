# SWP master/slave peripherals for Renode

A chip-agnostic model of the **ETSI TS 102 613 Single Wire Protocol** link for
[Renode](https://renode.io): one controller (the CLF), one slave (the UICC), a
raw TCP socket for host traffic, and the link-management timings that govern
the whole thing.

## What is and is not modelled

**Modelled** — the physical/link layer:

- the three interface states, **ACTIVATED / SUSPENDED / DEACTIVATED**
- the four transitions, **ACTIVATE / DEACTIVATE / SUSPEND / RESUME**
- the ETSI link-management timings **P1..P7**, every one of them configurable
- full-duplex raw byte transport, point-to-point, one master and one slave

**Not modelled, on purpose** — the protocol layer. No SOF/EOF, no CRC, no bit
stuffing, no HCI (TS 102 622), no gates or pipes. The peripherals carry opaque
bytes. Framing belongs to the two things at the ends of the wire: the host TCP
client at one end and the firmware at the other. Everything in `firmware/` and
`tools/java-client/` that looks like a frame is *their* framing, not the link's.

## Layout

```
peripherals/
  SWP.cs                      states, transitions, ISWPMaster/ISWPSlave,
                              SWPTimings (P1..P7), the virtual-time timer
  SWPController.cs            the master (CLF): owns the link and every
                              deadline; raw TCP socket for link data
  SWPSlave.cs                 the slave (UICC): passive on voltage, must answer
                              inside P6/P7 and P3; optional TCP socket. Designed
                              to be subclassed — see integrate.md
  SWPLoopbackSlave.cs         the smallest real subclass; a worked example and
                              the regression test for derivation
  SWPRegisterInterface.cs     memory-mapped front-end so firmware can drive
                              either end of the link
  SWPChipEventController.cs   drives the link from ChipEventController opcodes
  ChipEventController.cs      the stimulus framework this integrates with

platforms/
  swp_link.repl               a bare link: controller + slave, no CPU
  swp_chipevent.repl          the same, with event opcodes in front
  nucleo_h533re.repl          the STM32H533RE example board

scripts/                      .resc scripts for each of the above
firmware/                     bare-metal STM32H533RE demo (make)
tools/java-client/            a real Java TCP client (build.sh)
tests/                        Robot Framework suites
```

The peripherals are plain C# with no build step: the Renode Monitor compiles
them at load time via `include @peripherals/SWP.cs`. They also compile
unchanged inside the Renode source tree if you prefer that.

## Quick start

```bash
git clone --recurse-submodules https://github.com/renode/renode.git ~/renode
cd ~/renode && ./build.sh

cd /path/to/this/repo
~/renode/renode -e "path add @$PWD" -e "include @scripts/swp_link.resc"
```

Then, in the Monitor:

```
start
sysbus.swp Activate                      # DEACTIVATED -> ACTIVATED, honouring P5/P6/P7
sysbus.swp Transmit "00 A4 04 00 07"     # raw bytes towards the slave
sysbus.swp.uicc LastReceived             # what the slave got
sysbus.swp Status                        # state, timings, byte counters
```

Anything connected to `tcp://127.0.0.1:3460` gets the same data path: bytes
written there are transmitted to the slave, bytes the slave modulates back are
written out. The socket runs with Telnet mode **off**, so `0xFF` passes through
unescaped.

## The timings

Every P value is a property, read at the moment its deadline is armed — so
overriding one in a `.repl` or from the Monitor changes behaviour with no code
change:

```repl
swp: SWP.SWPController @ sysbus
    port: 3460
    P1: 20000    // idle window in ACTIVATED before the master may SUSPEND
    P2: 2000     // idle bits after the resume transition sequence
    P3: 500      // P3max: the slave must answer a RESUME inside this
    P4: 1000     // SWIO held low longer than this -> DEACTIVATED
    P5: 1000     // ACTIVATE: line settling before the slave is addressed
    P6: 10000    // ACTIVATE: the slave must answer inside this
    P7: 20000    // ACTIVATE: overall budget for the sequence
```

All values are microseconds.

**On the defaults.** The *semantics* of P1..P4 follow TS 102 613 clause 8
directly: P1 is the idle window after which the master may suspend, P2 the idle
bits that end a resume, P3max the slave's deadline to answer one, and P4 the
time SWIO must be held low to deactivate. P5..P7 cover the activation sequence,
where this model assigns the meanings documented in `SWPTimings`. The **numeric
defaults are engineering defaults, not quoted spec figures** — ETSI publishes
the normative table in clause 8, and it is paywalled. Set the properties from
your copy of the spec for a timing-exact model; nothing in the logic assumes
the shipped numbers.

## How the link behaves

Every diagram below is the shipped behaviour, not an idealisation of it: the
branches, the drops and the deadline names all correspond to code in
`peripherals/`.

### Who talks to whom

```mermaid
flowchart LR
    subgraph host["Host"]
        JC["TCP client<br/><i>builds and parses frames</i>"]
    end

    subgraph renode["Renode"]
        CE["SWPChipEventController<br/>0x07 / 0x08"]
        subgraph link["the SWP link — carries opaque bytes only"]
            CTRL["SWPController<br/>master / CLF"]
            SLV["SWPSlave<br/>slave / UICC"]
        end
        REG["SWPRegisterInterface<br/>CR SR TDR RDR"]
        FW["Firmware on the CPU<br/><i>builds and parses frames</i>"]
    end

    JC -->|"link events, port 3456"| CE
    CE -->|"ACTIVATE / DEACTIVATE"| CTRL
    JC <-->|"raw link data, port 3460"| CTRL
    CTRL <-->|"S1 voltage out, S2 current back"| SLV
    SLV <--> REG
    REG <-->|"memory-mapped"| FW
```

The two things that build and parse frames sit at the far ends. Everything
between them moves bytes it never looks at.

### State transitions

```mermaid
stateDiagram-v2
    [*] --> DEACTIVATED

    DEACTIVATED --> ACTIVATED : ACTIVATE — P5 settling,<br/>slave answers inside P6 and P7
    DEACTIVATED --> DEACTIVATED : ACTIVATE fails — P6 or P7 expired<br/>RESUME refused — no bit clock to restart

    ACTIVATED --> SUSPENDED : SUSPEND — explicit,<br/>or P1 of nothing but idle bits
    SUSPENDED --> ACTIVATED : RESUME — at the end of the<br/>P2 idle bits (P3max only checks the slave's answer)

    ACTIVATED --> DEACTIVATED : DEACTIVATE — SWIO held low for P4
    SUSPENDED --> DEACTIVATED : DEACTIVATE — SWIO held low for P4

    note left of DEACTIVATED
        S1 constantly low.
        No traffic either way.
    end note

    note right of SUSPENDED
        S1 constantly high.
        Link alive, no bit clock.
        Data on either side resumes it.
    end note

    note right of ACTIVATED
        Bit clock on S1.
        Full duplex: voltage out,
        current modulation back.
    end note
```

The master is the only side that decides the state. The slave mirrors what it
observes and has no vote — which is the wire's own arrangement, since it never
drives voltage.

### ACTIVATE

`DEACTIVATED` to `ACTIVATED`. P5, P6 and P7 are armed as **concurrent
deadlines**, not a sequence: P7 runs from t0 so an unusually long P5 cannot
hide a slave that never answers, and whichever of P6 and P7 expires first fails
the sequence.

```mermaid
sequenceDiagram
    autonumber
    participant H as Host client
    participant C as SWPController (CLF)
    participant S as SWPSlave (UICC)

    Note over C: state = DEACTIVATED, S1 held low
    H->>C: ACTIVATE
    C->>C: arm P7 (whole-sequence budget, from t0)
    C->>C: arm P5 (line settling)

    Note over C,S: t0 + P5 : line settled
    C->>S: OnActivate()
    C->>C: arm P6 (slave answer deadline)

    alt slave answers inside P6 and P7
        S->>S: wait ActivationResponseTime
        S->>S: state = ACTIVATED
        S-->>C: NotifySlaveActivated()
        C->>C: cancel P5/P6/P7
        C->>C: state = ACTIVATED, arm P1 idle watch
        C-->>H: acknowledged, link is up
    else P6 or P7 expires first
        C->>S: OnDeactivate()
        C->>C: state = DEACTIVATED
        C-->>H: ActivationFailed
    end
```

Two shortcuts the controller takes, both logged: `Activate()` on an already
`SUSPENDED` link is treated as a `RESUME`, and `Activate()` with no slave
registered fails immediately rather than waiting out P7.

### DEACTIVATE

Any state to `DEACTIVATED`. The master holds SWIO low; only after P4 is the
link observably down and the slave told.

```mermaid
sequenceDiagram
    autonumber
    participant H as Host client
    participant C as SWPController (CLF)
    participant S as SWPSlave (UICC)

    Note over C: state = ACTIVATED or SUSPENDED
    H->>C: DEACTIVATE
    C->>C: cancel every armed deadline
    C->>C: cancel the P1 idle watch
    C->>C: hold SWIO low, arm P4

    Note over C,S: t0 + P4 : held low long enough
    C->>S: OnDeactivate()
    S->>S: drop queued data, state = DEACTIVATED
    C->>C: state = DEACTIVATED
    C-->>H: acknowledged, link is down
```

### SUSPEND and RESUME

```mermaid
sequenceDiagram
    autonumber
    participant C as SWPController (CLF)
    participant S as SWPSlave (UICC)

    Note over C,S: SUSPEND — the master stops the bit clock

    alt automatic, after P1 of idle bits
        C->>C: P1 idle watch fires, no activity since it was armed
    else explicit
        Note over C: Suspend() from the Monitor, CR.SUSPEND or a host client
    end
    C->>S: OnSuspend()
    C->>C: state = SUSPENDED, S1 held high
    S->>S: state = SUSPENDED

    Note over C,S: RESUME — the master restarts it

    alt master-initiated
        Note over C: Resume(), or data to transmit while SUSPENDED
    else slave-initiated
        S->>C: RequestResume() — S2 modulation while suspended
    end
    C->>C: send transition sequence, arm P2

    Note over C,S: end of the last P2 idle bit
    C->>C: state = ACTIVATED, arm P3 and the P1 idle watch
    C->>S: OnResume()
    S->>S: state = ACTIVATED

    alt slave answers inside P3max
        S-->>C: NotifySlaveResumed()
        Note over C: transition sequence received, link healthy
    else P3max expires first
        C->>C: raise ResumeTimedOut
        Note over C: link stays ACTIVATED — the master owns the state,<br/>but the missed deadline is reported
    end
```

Note where the state actually changes on a resume: at the **end of the last P2
idle bit**, whether or not the slave has answered yet. P3max governs the
slave's transition sequence, and missing it is reported rather than fatal —
the master owns the link state either way.

### A message travelling master to slave

The host client writes raw bytes; the firmware reads them out of `RDR`. Nothing
in between inspects a byte.

```mermaid
sequenceDiagram
    autonumber
    participant H as Host TCP client
    participant SK as Socket reader thread
    participant C as SWPController (CLF)
    participant S as SWPSlave (UICC)
    participant R as SWPRegisterInterface
    participant F as Firmware

    H->>SK: write raw bytes to tcp://host:3460
    Note over SK: DataBlockReceived, off the emulation thread
    SK->>SK: copy the buffer (the provider reuses its own)
    SK->>C: HandleTimeDomainEvent(SendToSlave)

    Note over C: now on the emulation thread
    C->>C: charge transmission time at BitRate
    C->>S: ReceiveFromMaster(bytes)
    S->>S: BytesFromMaster += n, record burst
    S->>R: DataReceived event
    R->>R: push into the RX FIFO, raise RXNE
    R-->>F: IRQ, if RXNEIE is set
    F->>R: read RDR until RXLEVEL is 0
    Note over F: the firmware parses its own frames here —<br/>nothing below this line inspected a single byte
```

The branch logic behind that happy path:

```mermaid
flowchart TD
    A["SendToSlave(bytes)"] --> B{link state?}

    B -->|DEACTIVATED| C["drop, Overruns++<br/>no wire to put them on"]

    B -->|SUSPENDED| D{AutoResumeOnTransmit?}
    D -->|no| E["drop, Overruns++"]
    D -->|yes| F["queue in pendingToSlave"]
    F --> G["Resume()"]
    G --> H["end of P2 idle bits:<br/>state = ACTIVATED"]
    H --> I["FlushPendingToSlave()"]

    B -->|ACTIVATED| J["DeliverToSlave(bytes)"]
    I --> J

    J --> K{slave registered?}
    K -->|no| L["drop, Overruns++"]
    K -->|yes| M["BytesToSlave += n<br/>note activity, re-arm P1"]
    M --> N["schedule delivery after<br/>transmission time at BitRate"]
    N --> O{still ACTIVATED<br/>when it lands?}
    O -->|no| P["burst lost, logged"]
    O -->|yes| Q["slave.ReceiveFromMaster(bytes)"]
```

### A message travelling slave to master

Full duplex, so this path is independent of the one above and neither waits on
the other.

```mermaid
sequenceDiagram
    autonumber
    participant F as Firmware
    participant R as SWPRegisterInterface
    participant S as SWPSlave (UICC)
    participant C as SWPController (CLF)
    participant SK as Socket writer thread
    participant H as Host TCP client

    Note over F: the firmware builds its own frame
    loop one byte per TDR write
        F->>R: write TDR
        R->>S: SendToMaster(single byte)
        S->>S: BytesToMaster += 1
        S->>C: ReceiveFromSlave(bytes) — S2 current modulation
        C->>C: BytesFromSlave += n, note activity, re-arm P1
        C->>C: raise DataReceived
        alt a host client is connected
            C->>SK: Send(burst) — one write() per burst
            SK->>H: bytes appear on tcp://host:3460
        else nobody attached
            C->>C: drop, Overruns++
        end
    end
    Note over R,C: the register front-end sends a byte at a time, so each is<br/>its own burst. SendToMaster called with a larger array —<br/>from the Monitor, or the slave's own socket — sends it whole.
```

And its branch logic, including the slave-initiated resume — on the wire, the
UICC modulating S2 while suspended to ask for the bit clock back:

```mermaid
flowchart TD
    A["SendToMaster(bytes)"] --> B{master attached?}
    B -->|no| C["drop, Overruns++"]
    B -->|yes| D{link state?}

    D -->|DEACTIVATED| E["drop, Overruns++"]

    D -->|SUSPENDED| F["queue in pendingToMaster"]
    F --> G["master.RequestResume()<br/>S2 modulation asks for the bit clock back"]
    G --> H{master in SUSPENDED?}
    H -->|no| I["ignored — the master owns the state"]
    H -->|yes| J["Resume(): P2 idle bits, then ACTIVATED"]
    J --> K["OnResume() -> FlushPendingToMaster()"]

    D -->|ACTIVATED| L["BytesToMaster += n"]
    K --> L
    L --> M["master.ReceiveFromSlave(bytes)"]
    M --> N{master still ACTIVATED?}
    N -->|no| O["discarded, logged"]
    N -->|yes| P["DataReceived -> host socket"]
```

## ChipEventController integration

`SWPChipEventController` maps the existing stimulus opcodes onto the link:

| opcode | effect |
|---|---|
| `0x07` | ACTIVATE the link |
| `0x08` | DEACTIVATE the link |

The acknowledgement is held back until the link has genuinely settled, so a
successful `0x07` means the link is up — P5 elapsed and the slave answered
inside P6/P7 — not merely that the request was accepted. If the activation
fails, the reply's interface bitmask reports SWP as down rather than lying.

One thing worth knowing if you extend this: SWP's P-delays are *concurrent*
deadlines, not a sequence. An ACTIVATE arms P5, P6 and P7 at once and whichever
expires first decides the outcome, so they must not be routed onto a serial
queue such as `DelayedSequencer` — that would run the P7 guard before the P5
step and fail every activation. The acknowledgement is instead held by a
settle-watch that re-posts itself on the sequencer while the transition is in
flight.

## The NUCLEO-H533RE example

An STM32H533RE playing the **UICC** end of the link, with a host TCP client as
the CLF. The board reaches the link through `SWPRegisterInterface`, a small
memory-mapped block at the part's SWPMI address.

> On real silicon the STM32H5's block at `0x40008800` is SWPMI, a Single Wire
> Protocol *Master* Interface. This example fronts the slave end instead, so
> the board plays the UICC. Point `slave:` at `swp` in the `.repl` and the
> roles swap, with no change to the link model. The register layout is its own
> simple thing, not a claim to be register-compatible with ST's IP.

```bash
make -C firmware                       # builds firmware/build/swp-demo.elf
./tools/java-client/build.sh

# terminal 1
~/renode/renode -e "path add @$PWD" -e "include @scripts/nucleo_h533re_swp.resc" -e start

# terminal 2
java -cp tools/java-client/out swp.Main
```

Expected output from the client:

```
Connecting: events 127.0.0.1:3456, link 127.0.0.1:3460
  ping        -> Ok
  ACTIVATE    -> link is up
  -> 7E 03 01 02 03 48 7F
  <- 02 03 04  OK
  -> 7E 02 A0 A1 76 7F
  <- A1 A2  OK
  -> 7E 05 FF 00 FF 7E 7F 1C 7F
  <- 00 01 00 7F 80  OK
  DEACTIVATE  -> link is down
ALL EXCHANGES OK
```

The third exchange is the interesting one: `FF 00 FF 7E 7F` contains `0xFF` and
bytes that look like the demo's own SOF and EOF markers, and it reaches the
firmware unchanged — because the link never inspects payload and the framing
that does is length-delimited, above it.

### Register map

`SWPRegisterInterface`, all 32-bit:

| offset | name | access | contents |
|---|---|---|---|
| `0x00` | `CR` | W | bit0 ACTIVATE, bit1 DEACTIVATE, bit2 SUSPEND, bit3 RESUME (self-clearing); bit8 `RXNEIE`, bit9 `LINKIE` |
| `0x04` | `SR` | R/W1C | bits1:0 link state (0 DEACTIVATED, 1 SUSPENDED, 2 ACTIVATED); bit2 `RXNE`; bit3 `TXE`; bit4 `LINKCH` (w1c); bit5 `OVR` (w1c) |
| `0x08` | `TDR` | W | one byte to transmit |
| `0x0C` | `RDR` | R | one received byte; pops the RX FIFO |
| `0x10` | `RXLEVEL` | R | bytes queued in the RX FIFO |

IRQ is asserted while `(RXNE && RXNEIE) || (LINKCH && LINKIE)`.

## Building your own slave

`SWPSlave` is a base class, not just a reference implementation. Everything the
controller calls is `virtual`, and the seams a derivative needs are `protected`:
`Master` to answer on, `Timer` to schedule on virtual time, `SetState` to mirror
the link, `FlushPendingToMaster` to drain what queued while it was down. The two
hooks most real parts replace are `AnswerActivation()` and `AnswerResume()` —
the points where a part runs its own handshake — plus `ReceiveFromMaster()`.

```csharp
public class AcmeSecureElement : SWPSlave
{
    public AcmeSecureElement(IMachine machine, int port = 0, bool autoStart = true)
        : base(machine, port, autoStart) { }

    protected override void AnswerActivation()
    {
        // your ACT_SYNC / ACT_POWER_MODE exchange; the base still holds you to P6/P7
        SetState(SWPState.Activated, SWPTransition.Activate);
        Master?.NotifySlaveActivated();
        FlushPendingToMaster();
    }

    public override void ReceiveFromMaster(byte[] data)
    {
        base.ReceiveFromMaster(data);
        // your stack
    }
}
```

If your class already has a base class it must keep, implement `ISWPSlave`
instead — the controller only ever talks to the interface, and `ISWPEndpoint` is
wide enough that `SWPRegisterInterface` still works either way, so you keep the
memory-mapped block for your firmware.

One trap worth knowing before you start: the controller holds the slave as an
`ISWPSlave` and calls through the interface, so a `new` method instead of an
`override` compiles, reads correctly, and never runs. **[integrate.md](integrate.md)**
covers this and the rest of the process in full.

## Tests

```bash
RENODE_ROOT=~/renode ./run-tests.sh
```

Two suites, 35 cases, all passing:

- `tests/swp.robot` (28) — every state, every transition, each timing boundary
  (P5 before the slave is addressed, P6/P7 activation failure, the end of the
  P2 idle bits, P3max, P4 before deactivation takes effect), the full-duplex
  byte path, payload transparency, and runtime reconfiguration of the timings.
- `tests/nucleo_h533re_swp.robot` (7) — the example end to end: firmware boot,
  link state visible through the register block, a framed request answered, and
  the ChipEventController opcodes.

Three of those cases exist specifically to protect derivation: an override is
actually called through the controller, the base class does *not* do the same
thing on its own, and a slave reached only through `ISWPSlave` still drives the
register block.

Every timing assertion advances virtual time with `emulation RunFor`, never a
host sleep, so the suite is deterministic and replayable.

## Reference

`docs/swp-protocol-reference.md` is a protocol-level primer on SWP: the
single-wire full-duplex trick, the C6 contact, framing, the HCI layer above,
and a debugging checklist.
