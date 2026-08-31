//
// SWPLoopbackSlave.cs — the smallest possible proprietary slave, and the proof
// that deriving from SWPSlave actually works.
//
// It exists for two reasons:
//
//   1. Tests need a slave that answers without any firmware behind it, and that
//      affordance does not belong in the base class a real part derives from.
//
//   2. It is the worked example integrate.md points at. If overriding
//      ReceiveFromMaster here did not take effect, nothing you write would
//      either — the controller holds every slave as an ISWPSlave and calls
//      through the interface, so `new` silently binds to the base. That this
//      class echoes at all is the regression test for that.
//
// Your own slave replaces the body of ReceiveFromMaster with your stack, and
// usually overrides AnswerActivation as well. Everything else — the P3/P6/P7
// discipline, the queue-and-resume paths, the state mirroring — comes from the
// base and is what you are reusing.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.SWP
{
    /// <summary>
    /// A slave that modulates every burst it receives straight back:
    ///
    ///     swp:  SWP.SWPController @ sysbus
    ///     uicc: SWP.SWPLoopbackSlave @ swp
    ///
    /// A real UICC answers with content rather than a mirror, so this is a test
    /// double — but it is a real subclass, built through the same seams a
    /// proprietary part uses.
    /// </summary>
    public class SWPLoopbackSlave : SWPSlave
    {
        public SWPLoopbackSlave(IMachine machine, int port = 0, bool autoStart = true)
            : base(machine, port, autoStart)
        {
        }

        /// <summary>
        /// Echo the burst back. Turn it off to use this class as a plain
        /// recording slave.
        /// </summary>
        public bool Loopback { get; set; } = true;

        /// <summary>
        /// The override that matters: the controller calls this through
        /// <see cref="ISWPSlave"/>, so it only runs because the base declares it
        /// virtual. Chain to the base first — it keeps the byte counters, the
        /// traffic record and the <see cref="SWPSlave.DataReceived"/> event
        /// working — then add your own behaviour.
        /// </summary>
        public override void ReceiveFromMaster(byte[] data)
        {
            base.ReceiveFromMaster(data);

            if(!Loopback || data == null || data.Length == 0)
            {
                return;
            }

            this.Log(LogLevel.Noisy, "Loopback: echoing {0} byte(s) back to the master", data.Length);
            SendToMaster(data);
        }
    }
}
