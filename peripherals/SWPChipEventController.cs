//
// SWPChipEventController.cs — wires an SWPController to the existing
// ChipEventController stimulus framework.
//
// The ChipEventController already owns the shape of this problem: an opcode
// arrives over TCP, becomes an event on the emulation thread, and the client's
// acknowledgement is held back until the chip has finished reacting, including
// any virtual-time delays it scheduled. SWP activation *is* a virtual-time
// delay — P5 for the line to settle, then up to P6/P7 waiting for the slave —
// so the two fit together with no new machinery:
//
//   PowerUpSWP   (opcode 0x07) -> ACTIVATE   the link
//   PowerDownSWP (opcode 0x08) -> DEACTIVATE the link
//
// The glue that matters is how the acknowledgement is held back. Activate()
// returns immediately — it only arms P5, P6 and P7 — so acknowledging when it
// returns would tell the client the link was up long before it was. The fix is
// a settle-watch: a step that re-posts itself on the base class's
// DelayedSequencer while the transition is still in flight. The sequencer stays
// Busy for as long as it keeps re-posting, which is exactly the condition the
// base class's ticket handshake waits on, so the client's reply lands when the
// link has genuinely settled.
//
// What must NOT be done is routing the controller's own P-delays onto that
// sequencer. Those are concurrent deadlines, not a sequence: a serial queue
// would run the P7 guard before the P5 step and fail every activation.
//
// This class is chip-agnostic; SupportsInterface reports SWP only. A real chip
// with more interfaces should subclass it, or copy the four lines of glue in
// the constructor into its own ChipEventControllerBase subclass.
//
using System;
using System.Text;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Peripherals.SWP;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.SWP
{
    /// <summary>
    /// Drives an <see cref="SWPController"/> from ChipEventController opcodes.
    ///
    ///     swp:       SWP.SWPController @ sysbus
    ///         port: 3460
    ///     uicc:      SWP.SWPSlave @ swp
    ///     chipEvents: SWP.SWPChipEventController @ sysbus
    ///         controller: swp
    ///         controlPort: 3456
    ///
    /// Then `0x07` on the event port activates the link and `0x08` deactivates
    /// it, each acknowledged only once the transition has completed.
    /// </summary>
    public class SWPChipEventController : ChipEventControllerBase
    {
        public SWPChipEventController(IMachine machine, SWPController controller, int controlPort = 3456,
            int sessionPortBase = 0, int maxSessions = 16, int unclaimedSessionTimeout = 60,
            bool autoStart = true, int completionTimeout = 5)
            : base(machine, controlPort, sessionPortBase, maxSessions, unclaimedSessionTimeout, autoStart,
                completionTimeout)
        {
            if(controller == null)
            {
                throw new ConstructionException("SWPChipEventController needs a controller to drive");
            }
            this.controller = controller;

            controller.StateChanged += OnLinkStateChanged;
            controller.ActivationFailed += OnActivationFailed;
        }

        /// <summary>This part has an SWP interface and nothing else.</summary>
        public override bool SupportsInterface(ChipInterface iface) => iface == ChipInterface.SWP;

        /// <summary>Opcode 0x07. Acknowledged once the link reaches ACTIVATED or the activation fails.</summary>
        public override void PowerUpSWP()
        {
            this.Log(LogLevel.Debug, "PowerUpSWP: activating the SWP link");
            lastActivationFailed = false;
            controller.Activate();

            // P7 bounds the whole activation, so nothing can still be in flight
            // a little past it.
            HoldUntilSettled(() => controller.State == SWPState.Activated || lastActivationFailed,
                controller.Timings.P7 + Margin);
        }

        /// <summary>Opcode 0x08. Acknowledged once the link has been held low for P4 and is DEACTIVATED.</summary>
        public override void PowerDownSWP()
        {
            this.Log(LogLevel.Debug, "PowerDownSWP: deactivating the SWP link");
            controller.Deactivate();

            HoldUntilSettled(() => controller.State == SWPState.Deactivated,
                controller.Timings.P4 + Margin);
        }

        /// <summary>
        /// Keep the base class's sequencer Busy — and so the client's
        /// acknowledgement pending — until <paramref name="settled"/> is true or
        /// the budget runs out. Each poll re-posts the next one, so an idle
        /// watch costs nothing once the transition has resolved.
        /// </summary>
        private void HoldUntilSettled(Func<bool> settled, TimeInterval budget)
        {
            var remaining = budget;
            Action poll = null;
            poll = () =>
            {
                if(settled())
                {
                    return;
                }
                if(remaining <= PollInterval)
                {
                    this.Log(LogLevel.Warning,
                        "Link did not settle within {0}us; acknowledging the client anyway",
                        budget.TotalMicroseconds);
                    return;
                }
                remaining -= PollInterval;
                Sequencer.Post(DelayedStep.After(PollInterval, poll));
            };
            Sequencer.Post(DelayedStep.After(PollInterval, poll));
        }

        // --- extra Monitor verbs, beyond the two power opcodes ------------------

        /// <summary>`sysbus.chipEvents SuspendLink`</summary>
        public string SuspendLink()
        {
            Machine.HandleTimeDomainEvent<object>(_ => controller.Suspend(), null, false);
            return controller.State.ToString();
        }

        /// <summary>`sysbus.chipEvents ResumeLink`</summary>
        public string ResumeLink()
        {
            Machine.HandleTimeDomainEvent<object>(_ => controller.Resume(), null, false);
            return controller.State.ToString();
        }

        /// <summary>`sysbus.chipEvents LinkState`</summary>
        public string LinkState() => controller.State.ToString();

        /// <summary>True if the most recent ACTIVATE gave up on P6/P7 rather than coming up.</summary>
        public bool LastActivationFailed => lastActivationFailed;

        public override void Reset()
        {
            base.Reset();
            lastActivationFailed = false;
        }

        public override void Dispose()
        {
            controller.StateChanged -= OnLinkStateChanged;
            controller.ActivationFailed -= OnActivationFailed;
            base.Dispose();
        }

        private void OnLinkStateChanged(SWPStateChangedEventArgs args)
        {
            this.Log(LogLevel.Debug, "SWP link {0}", args);
        }

        private void OnActivationFailed()
        {
            lastActivationFailed = true;
            this.Log(LogLevel.Warning, "SWP activation failed; the link stays DEACTIVATED");

            // The server optimistically marked SWP as powered when the handler
            // returned. SWP is the only interface this class claims, so clearing
            // the whole bitmask clears exactly that bit — and it happens while
            // the settle-watch still holds the ticket, so the client's reply
            // carries the corrected mask rather than a false success.
            Server.ResetState();
        }

        /// <summary>How often the settle-watch re-checks, in virtual time.</summary>
        private static readonly TimeInterval PollInterval = TimeInterval.FromMicroseconds(200);

        /// <summary>Slack past a transition's own budget, so a deadline landing
        /// exactly on the boundary is not called a timeout.</summary>
        private static readonly TimeInterval Margin = TimeInterval.FromMilliseconds(5);

        private readonly SWPController controller;
        private volatile bool lastActivationFailed;
    }
}
