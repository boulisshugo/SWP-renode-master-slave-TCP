package swp;

import java.io.IOException;

/**
 * End-to-end exercise of a running Renode SWP link.
 *
 *   1. Activate the link over the ChipEventController port. The reply does not
 *      come back until the link has genuinely reached ACTIVATED, so there is
 *      nothing to poll and no sleep to tune.
 *   2. Send framed requests over the raw link port and check the answers the
 *      firmware sends back.
 *   3. Deactivate.
 *
 * Usage:  java -cp out swp.Main [host] [eventPort] [linkPort]
 * Exit code 0 if every exchange checked out, 1 otherwise.
 */
public final class Main {

    private static final String DEFAULT_HOST = "127.0.0.1";
    private static final int DEFAULT_EVENT_PORT = 3456;
    private static final int DEFAULT_LINK_PORT = 3460;
    private static final int CONNECT_TIMEOUT_MS = 5000;
    private static final int REPLY_TIMEOUT_MS = 10000;

    public static void main(String[] args) {
        String host = args.length > 0 ? args[0] : DEFAULT_HOST;
        int eventPort = args.length > 1 ? Integer.parseInt(args[1]) : DEFAULT_EVENT_PORT;
        int linkPort = args.length > 2 ? Integer.parseInt(args[2]) : DEFAULT_LINK_PORT;

        try {
            System.exit(run(host, eventPort, linkPort) ? 0 : 1);
        } catch (Exception e) {
            System.err.println("FAILED: " + e);
            e.printStackTrace();
            System.exit(1);
        }
    }

    private static boolean run(String host, int eventPort, int linkPort) throws IOException {
        boolean ok = true;

        System.out.printf("Connecting: events %s:%d, link %s:%d%n", host, eventPort, host, linkPort);

        try (SwpEventClient events = new SwpEventClient(host, eventPort, CONNECT_TIMEOUT_MS);
             SwpLinkClient link = new SwpLinkClient(host, linkPort, 500)) {

            int[] ping = events.send(SwpEventClient.OP_PING);
            System.out.printf("  ping        -> %s%n", SwpEventClient.describeStatus(ping[0]));

            // Blocks until the link is up: P5 settling plus the slave's answer
            // inside P6/P7 all happen before this returns.
            if (events.activate()) {
                System.out.println("  ACTIVATE    -> link is up");
            } else {
                System.out.println("  ACTIVATE    -> FAILED");
                return false;
            }

            byte[][] payloads = {
                { 0x01, 0x02, 0x03 },
                { (byte) 0xA0, (byte) 0xA1 },
                // 0xFF and 0x00 must survive untouched: the socket runs with
                // Telnet mode off, so nothing escapes 0xFF on the way through.
                { (byte) 0xFF, 0x00, (byte) 0xFF, 0x7E, 0x7F },
            };

            for (byte[] payload : payloads) {
                ok &= exchange(link, payload);
            }

            if (events.deactivate()) {
                System.out.println("  DEACTIVATE  -> link is down");
            } else {
                System.out.println("  DEACTIVATE  -> FAILED");
                ok = false;
            }
        }

        System.out.println(ok ? "ALL EXCHANGES OK" : "SOME EXCHANGES FAILED");
        return ok;
    }

    /** Send one frame and check the firmware's answer: each payload byte + 1. */
    private static boolean exchange(SwpLinkClient link, byte[] payload) throws IOException {
        SwpFrame request = new SwpFrame(payload);
        System.out.printf("  -> %s%n", SwpFrame.hex(request.encode()));
        link.send(request);

        SwpFrame reply;
        try {
            reply = link.receive(REPLY_TIMEOUT_MS);
        } catch (IllegalStateException e) {
            System.out.printf("     malformed reply: %s%n", e.getMessage());
            return false;
        }

        if (reply == null) {
            System.out.println("     no reply within the timeout");
            return false;
        }

        byte[] expected = new byte[payload.length];
        for (int i = 0; i < payload.length; i++) {
            expected[i] = (byte) (payload[i] + 1);
        }

        byte[] got = reply.payload();
        boolean match = java.util.Arrays.equals(expected, got);
        System.out.printf("  <- %s  %s%n", SwpFrame.hex(got),
                match ? "OK" : "MISMATCH, expected " + SwpFrame.hex(expected));
        return match;
    }
}
