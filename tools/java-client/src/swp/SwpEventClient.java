package swp;

import java.io.Closeable;
import java.io.DataInputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;

/**
 * Drives the SWP link over the ChipEventController port.
 *
 * One byte in, two bytes out: {@code [status, interfaceBitmask]}. The reply is
 * held back until the chip has finished reacting, so a successful ACTIVATE
 * means the link really is up — including the P5 settling time and the slave's
 * answer inside P6/P7 — not merely that the request was accepted.
 */
public final class SwpEventClient implements Closeable {

    /** Opcodes from ChipEventOpcode; the SWP interface is bit 3 of the mask. */
    public static final int OP_PING           = 0x00;
    public static final int OP_POWER_UP_SWP   = 0x07;
    public static final int OP_POWER_DOWN_SWP = 0x08;
    public static final int OP_QUERY_STATUS   = 0xFF;

    public static final int SWP_BIT = 1 << 3;

    private final Socket socket;
    private final DataInputStream in;
    private final OutputStream out;

    public SwpEventClient(String host, int port, int timeoutMillis) throws IOException {
        socket = new Socket();
        socket.connect(new InetSocketAddress(host, port), timeoutMillis);
        socket.setTcpNoDelay(true);
        socket.setSoTimeout(timeoutMillis);
        in = new DataInputStream(socket.getInputStream());
        out = socket.getOutputStream();
    }

    /** @return {@code [status, interfaceBitmask]}. */
    public int[] send(int opcode) throws IOException {
        out.write(opcode);
        out.flush();
        int status = in.readUnsignedByte();
        int mask = in.readUnsignedByte();
        return new int[] { status, mask };
    }

    /** @return true once the SWP bit is set in the returned status mask. */
    public boolean activate() throws IOException {
        int[] reply = send(OP_POWER_UP_SWP);
        return reply[0] == 0x00 && (reply[1] & SWP_BIT) != 0;
    }

    public boolean deactivate() throws IOException {
        int[] reply = send(OP_POWER_DOWN_SWP);
        return reply[0] == 0x00 && (reply[1] & SWP_BIT) == 0;
    }

    public static String describeStatus(int status) {
        switch (status) {
            case 0x00: return "Ok";
            case 0x01: return "NoChange";
            case 0xE0: return "UnknownOpcode";
            case 0xE1: return "NoFreeSession";
            case 0xE2: return "InternalError";
            case 0xE3: return "NoSuchSession";
            case 0xE4: return "Unsupported";
            case 0xE5: return "Timeout";
            default:   return String.format("unknown(0x%02X)", status);
        }
    }

    @Override
    public void close() throws IOException {
        socket.close();
    }
}
