---
name: swp-protocol
description: >-
  General reference for SWP (Single Wire Protocol, ETSI TS 102 613): the
  physical link between a mobile phone's Contactless Front-end (CLF / NFC
  controller) and the UICC/eUICC (SIM) that carries NFC card-emulation
  traffic to a SIM-hosted secure element. Covers the single-wire
  full-duplex trick — the CLF drives voltage states S1/S2 to send data
  one way while the UICC modulates *current* on the same wire to send
  data the other way, simultaneously; the C6 contact reuse on the SIM;
  bit encoding and frame format (SOF/payload/CRC/EOF with bit stuffing);
  the HCI data-link layer that runs on top (ETSI TS 102 622) with its
  hosts/gates/pipes model; the activation sequence (ACT_READY /
  ACT_POWER_MODE); and the resulting stack used for SWP-SIM NFC payments.
  Use this whenever the user reasons about SWP: bringing up NFC on a
  handset, decoding an SWP capture, debugging why the SIM never
  activates on the C6 pin, understanding how APDUs from an NFC reader
  end up at a SIM applet, or comparing SWP to 1-Wire / ISO 7816 / LIN.
  Trigger on "SWP", "Single Wire Protocol", "SWP-SIM", "CLF", "UICC" in
  an NFC context, "C6 pin", "HCI" in an NFC/ETSI context (not HDMI-CEC
  or USB), "ETSI 102 613" / "ETSI 102 622", "S1 state" / "S2 state",
  "ACT_READY", or "contactless front-end". Prefer this over guessing,
  since SWP's simultaneous voltage-and-current signaling is unusual and
  frequently mis-described as time-multiplexed.
compatibility: "Protocol-level reference; handset/SIM/HAL agnostic"
---

# SWP (Single Wire Protocol) — Protocol Reference

SWP is the ETSI-standardized physical and link layer that connects a
mobile phone's **Contactless Front-end (CLF)** — the NFC controller — to
the **UICC** (the SIM, or an eUICC/iUICC equivalent). Its whole purpose
is to let an NFC reader in the field talk to a *secure element hosted on
the SIM*, so that card-emulation transactions (transit, payment,
building access) can be signed by keys that never leave the SIM.

Two ETSI specs together define the stack:

- **ETSI TS 102 613** — SWP itself: the physical/electrical layer and
  frame format on the single wire.
- **ETSI TS 102 622** — **HCI** (Host Controller Interface), the data
  link / routing layer that runs on top of SWP and moves application
  data (ultimately ISO 7816 APDUs and ISO 14443 frames) between the
  CLF, the UICC, and any other hosts.

Note the acronym collision: "HCI" here is ETSI's NFC host-controller
interface, not USB HCI and not Bluetooth HCI. In an SWP context, "HCI"
always means TS 102 622.

## 1. Where SWP physically lives

SWP reuses the previously-unassigned **C6** contact on the SIM card
(the ISO 7810 contact plate that ISO 7816 left as RFU). C1/C5/C7 keep
their normal ISO 7816 meaning — power, ground, and the async serial I/O
that the phone's baseband uses to talk to the SIM for GSM/UMTS/LTE — and
SWP runs entirely on C6 in parallel. This means the SIM is
simultaneously an ISO 7816 target on C7 (talking to the baseband) and
an SWP target on C6 (talking to the CLF), and the two links are
independent.

The link is strictly point-to-point: one CLF, one UICC, one wire. No
addressing at the physical layer; addressing happens at HCI (§5).

## 2. The single-wire full-duplex trick

This is the mechanism people most often get wrong, so it's worth
stating explicitly: SWP is **full duplex on one wire, achieved by using
two different physical quantities in the two directions at the same
time.** It is *not* time-multiplexed half-duplex.

- **CLF → UICC** direction uses **voltage**. The CLF drives the wire
  between two levels called **S1** (nominally high, near V_CLF) and
  **S2** (nominally low, near ground). The stream of S1/S2 states, with
  the encoding in §3, carries the CLF's outgoing bits.
- **UICC → CLF** direction uses **current** on the same wire, at the
  same time. During the CLF's **S1** intervals, the UICC either draws
  extra current or doesn't; the CLF's front-end senses that current
  modulation and recovers the UICC's outgoing bits from it.

The CLF is the "master" of the link in the sense that it provides the
voltage and clocks the state transitions. The UICC is passive on
voltage — it only ever modulates current — but its current-modulation
timing is derived from the same edges the CLF drives, so both
directions share one implicit clock.

Consequences:

- The link cannot function while the CLF holds the wire at S2 for the
  UICC's reply — the UICC needs S1 intervals to modulate against.
  Encoding (§3) is designed so S1 states occur frequently enough to
  carry the return channel.
- Because the return channel is a small current delta, SWP is more
  susceptible to load-related noise than a voltage-signaled bus of
  similar speed. Handset layout around the SIM cage matters.

## 3. Bit encoding and framing

Bits are encoded so that every bit period contains at least one S1
sub-interval (giving the UICC an opportunity to modulate current for
its return bit). At the wire level a `1` and a `0` are distinguished
by *where in the bit cell* the S2 sub-interval falls — conceptually a
pulse-position encoding, not straight NRZ. The nominal bit rate is
negotiated during activation; SWP defines several rates up to roughly
**1.7 Mbit/s**, with lower fallback rates for constrained
implementations.

Frames on top of that bit stream have a fixed shape:

```
| SOF | Payload | CRC | EOF |
```

- **SOF / EOF** are dedicated non-data patterns on the wire that can't
  be confused with encoded bits.
- **Payload** is the HCI packet being carried (§5), byte-oriented.
- **CRC** covers the payload for error detection.
- **Bit stuffing** inside the payload prevents any legitimate data
  pattern from ever looking like an SOF/EOF marker.

There is no ARQ at the SWP layer itself — a CRC-failing frame is
dropped and the loss is dealt with by the layer above (HCI, or the
application on top of HCI, per how the specific gate is configured).

## 4. Activation and power modes

The link is not "always on." SWP defines an explicit activation
handshake and a small set of power modes:

- **Activation** starts when the CLF begins driving S1/S2 and sends
  the **ACT_READY** signal; the UICC responds by modulating current to
  indicate it is present and ready. Until this exchange completes, no
  HCI traffic flows.
- **ACT_POWER_MODE** exchanges let the CLF tell the UICC what power
  envelope it currently has available — critically, whether the
  handset is powered normally, in a low-power state, or **operating
  from the RF field alone** (battery-off card emulation, so a dead
  phone can still tap through a turnstile). The UICC uses this to
  decide which applets/services it will make available on the link.
- **Deactivation** returns the wire to an idle state; the UICC's ISO
  7816 link on C7 is unaffected by SWP activation state either way.

Battery-off / field-powered operation is one of the main product
reasons SWP exists — an alternative NFC-secure-element architecture
using an embedded SE would lose the "works when the phone is dead"
property if the SE weren't wired to be powered by the CLF's harvested
RF energy.

## 5. HCI on top: hosts, gates, and pipes

HCI (TS 102 622) is the data link that runs over SWP frames and gives
the software above it something addressable to talk to. Its model:

- **Host**: a logical endpoint. The CLF is one host; the UICC is
  another; an embedded secure element (if present and bridged) can be
  another. Each host has a **Host ID**.
- **Gate**: a service exposed by a host — e.g. a "card RF gate" that
  represents an ISO 14443-A card-emulation endpoint, or an
  "administration gate" for link management. Each gate has a **Gate
  ID**.
- **Pipe**: a bidirectional channel between one gate on one host and
  one gate on another host, identified by a **Pipe ID**. All
  application traffic (commands, events, responses) travels over
  pipes.

Pipe setup goes through the administration gate: the UICC and CLF
negotiate which pipes exist and which gate-to-gate pairs they connect,
then application data flows. For NFC card emulation, an inbound ISO
14443 frame from an external reader is delivered by the CLF into the
relevant card RF gate's pipe, ends up at the UICC's application, is
handed to the correct applet, and the response makes the return trip
— all as HCI events/responses over SWP frames on C6.

## 6. What actually goes over the link, end to end

For a contactless payment tap, the stack looks approximately like:

```
NFC reader   <-- ISO 14443 (RF) -->   CLF (in the phone)
                                       |
                                       |  HCI over SWP (C6 pin)
                                       v
                                      UICC / SIM
                                       |
                                       |  logical channel to applet
                                       v
                                     Payment applet (EMV, etc.)
```

The APDU exchange the reader thinks it's having with a card is really
happening with a SIM applet, tunneled reader→CLF→SWP→HCI→UICC. The CLF
does the radio, the SIM does the crypto, and SWP is the pipe between
them.

## 7. How SWP relates to other single-wire / smart-card links

Useful contrasts if you're coming from another bus:

| | SWP | ISO 7816 (SIM's C7 line) | 1-Wire (Dallas) | LIN |
|---|---|---|---|---|
| Wires | 1 (plus GND, plus C1 power) | 1 (plus GND/VCC/CLK/RST) | 1 (plus GND; parasitic power option) | 1 (plus GND) |
| Duplex | Full (voltage one way, current the other, simultaneously) | Half | Half | Half |
| Topology | Point-to-point | Point-to-point | Multi-drop | Multi-drop |
| Speed | Up to ~1.7 Mbit/s | ~9.6 kbit/s to ~1 Mbit/s (depending on class) | ~15 kbit/s standard / ~125 kbit/s overdrive | ~20 kbit/s max |
| Primary use | NFC ↔ SIM secure element | Baseband ↔ SIM APDU exchange | Sensors, ID buttons, EEPROMs | Automotive body electronics |
| Layer above | HCI (ETSI 102 622) | ISO 7816-4 APDUs | Vendor-specific | LIN frame + scheduler |

"Single wire" appears in several unrelated buses; SWP specifically is
the ETSI phone-to-SIM one, and only SWP uses the simultaneous
voltage-plus-current duplexing scheme.

## 8. Debugging checklist

```
[ ] Confirmed C6 is actually routed on the handset — some low-end
    boards don't wire C6 to the CLF at all, in which case no SWP is
    ever going to activate no matter what the software does
[ ] SIM/UICC actually supports SWP (older SIMs and some MVNO-issued
    SIMs don't) — check ATR/EF_DIR or issuer documentation
[ ] CLF and UICC agreed on a bit rate during activation; a rate
    mismatch usually manifests as ACT_READY never getting an answer
[ ] ACT_POWER_MODE reflects the actual handset state — a UICC told
    "full power" while the phone is really in battery-off will refuse
    to enable the applets that need it, and vice versa
[ ] HCI pipe setup completed before expecting application traffic —
    a missing/mis-numbered pipe looks exactly like a dead link from
    the app layer
[ ] For "works powered, fails battery-off" symptoms, check the
    field-power path independently from the SWP link itself — SWP
    can be fine while the SE simply isn't getting enough harvested
    energy from the antenna
[ ] Distinguished SWP-layer CRC errors (dropped frames, retried by
    upper layer) from HCI-layer errors (bad pipe/gate IDs, unknown
    events) — the fix is in a different spec
[ ] Not confusing SWP HCI (TS 102 622) with USB HCI or Bluetooth
    HCI when reading logs; all three abbreviate the same way
```

Symptom shortcuts: link never activates -> almost always a wiring or
UICC-support issue, not software; activates but no card emulation ->
HCI pipe setup incomplete or applet not selected; works on the bench
but not at a real terminal -> field-power / battery-off path, not SWP;
sporadic CRC errors under load -> SIM cage / C6 routing noise, look
at layout before firmware.

## 9. Quick reference

| Question | Answer |
|---|---|
| What is SWP *for*? | Connecting an NFC controller (CLF) in a handset to a secure element on the SIM (UICC), so card-emulation transactions can be signed by SIM-hosted keys. |
| Which spec? | ETSI TS 102 613 for the wire; ETSI TS 102 622 (HCI) for the layer above. |
| Which SIM contact? | C6, the ISO 7816 RFU contact. C7 keeps its normal baseband-to-SIM role in parallel. |
| Full or half duplex? | Full — CLF drives voltage (S1/S2) one way, UICC modulates current the other way, simultaneously on the same wire. |
| Master/slave? | CLF drives voltage and timing; UICC is passive on voltage and only ever modulates current. Point-to-point, not multi-drop. |
| Nominal speed? | Up to roughly 1.7 Mbit/s, negotiated at activation; lower rates supported for constrained implementations. |
| How is a payment applet reached? | ISO 14443 frame at the CLF → HCI event over SWP → UICC's card RF gate → applet APDU handler → response back the same path. |
| Does it work with a dead battery? | Yes — that's a core design goal. ACT_POWER_MODE tells the UICC when it's running on RF-harvested power only. |
| Related to 1-Wire (Dallas)? | No — unrelated protocols that happen to share the "single wire" description. |
| Related to ISO 7816 on C7? | Physically adjacent, logically independent. The two links can be active simultaneously with different traffic. |