package swp;

import java.util.Arrays;

/**
 * Framing for the SWP demo, and the reason it lives here rather than in the
 * Renode peripherals: the link model carries opaque bytes, so building and
 * checking frames is the job of the two things at the ends of the wire — this
 * client at one end, the firmware at the other.
 *
 * The shape mirrors ETSI TS 102 613's {@code SOF | payload | CRC | EOF},
 * reduced to what a demo needs:
 *
 * <pre>
 *     SOF (0x7E) | LEN | PAYLOAD (LEN bytes) | CRC8 | EOF (0x7F)
 * </pre>
 *
 * Real SWP uses bit stuffing and a CRC-16; neither belongs in an example whose
 * point is the link layer underneath. Change this class and the firmware's
 * matching parser together — nothing in Renode needs to know.
 */
public final class SwpFrame {

    public static final int SOF = 0x7E;
    public static final int EOF = 0x7F;
    public static final int MAX_PAYLOAD = 64;

    private final byte[] payload;

    public SwpFrame(byte[] payload) {
        if (payload.length == 0 || payload.length > MAX_PAYLOAD) {
            throw new IllegalArgumentException(
                    "payload must be 1.." + MAX_PAYLOAD + " bytes, got " + payload.length);
        }
        this.payload = payload.clone();
    }

    public byte[] payload() {
        return payload.clone();
    }

    /** The bytes to put on the wire. */
    public byte[] encode() {
        byte[] out = new byte[payload.length + 4];
        out[0] = (byte) SOF;
        out[1] = (byte) payload.length;
        System.arraycopy(payload, 0, out, 2, payload.length);
        out[payload.length + 2] = crc8(payload);
        out[payload.length + 3] = (byte) EOF;
        return out;
    }

    /** CRC-8, polynomial 0x07, zero seed — the usual smart-card choice. */
    public static byte crc8(byte[] data) {
        int crc = 0;
        for (byte b : data) {
            crc ^= (b & 0xFF);
            for (int bit = 0; bit < 8; bit++) {
                crc = ((crc & 0x80) != 0) ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
            }
        }
        return (byte) crc;
    }

    public static String hex(byte[] data) {
        StringBuilder sb = new StringBuilder();
        for (byte b : data) {
            if (sb.length() > 0) {
                sb.append(' ');
            }
            sb.append(String.format("%02X", b));
        }
        return sb.toString();
    }

    @Override
    public String toString() {
        return "SwpFrame[" + hex(payload) + "]";
    }

    @Override
    public boolean equals(Object other) {
        return other instanceof SwpFrame && Arrays.equals(payload, ((SwpFrame) other).payload);
    }

    @Override
    public int hashCode() {
        return Arrays.hashCode(payload);
    }

    /**
     * Incremental parser. TCP is a stream, so a frame can arrive split across
     * reads or sharing a read with the next one — feed every byte through here
     * rather than assuming one read is one frame.
     */
    public static final class Parser {

        private enum State { WAIT_SOF, WAIT_LEN, WAIT_PAYLOAD, WAIT_CRC, WAIT_EOF }

        private State state = State.WAIT_SOF;
        private byte[] buffer = new byte[0];
        private int expected;
        private int received;
        private int crc;

        /** @return the completed frame, or null if more bytes are needed. */
        public SwpFrame feed(int b) {
            b &= 0xFF;
            switch (state) {
                case WAIT_SOF:
                    if (b == SOF) {
                        state = State.WAIT_LEN;
                    }
                    return null;

                case WAIT_LEN:
                    if (b == 0 || b > MAX_PAYLOAD) {
                        state = State.WAIT_SOF;   // nonsense length: resynchronise
                        return null;
                    }
                    expected = b;
                    received = 0;
                    buffer = new byte[expected];
                    state = State.WAIT_PAYLOAD;
                    return null;

                case WAIT_PAYLOAD:
                    buffer[received++] = (byte) b;
                    if (received == expected) {
                        state = State.WAIT_CRC;
                    }
                    return null;

                case WAIT_CRC:
                    crc = b;
                    state = State.WAIT_EOF;
                    return null;

                case WAIT_EOF:
                    state = State.WAIT_SOF;
                    if (b != EOF) {
                        throw new IllegalStateException("frame did not end with EOF");
                    }
                    if (crc != (crc8(buffer) & 0xFF)) {
                        throw new IllegalStateException(String.format(
                                "CRC mismatch: frame carried %02X, payload computes %02X",
                                crc, crc8(buffer)));
                    }
                    return new SwpFrame(buffer);

                default:
                    throw new IllegalStateException("unreachable");
            }
        }
    }
}
