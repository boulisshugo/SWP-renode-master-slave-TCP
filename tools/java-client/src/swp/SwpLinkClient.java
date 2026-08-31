package swp;

import java.io.Closeable;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;

/**
 * The raw link socket: bytes written here are transmitted to the SWP slave,
 * and bytes the slave modulates back arrive here. Nothing on the Renode side
 * inspects them, which is why this class deals in frames while the peripheral
 * deals in bytes.
 */
public final class SwpLinkClient implements Closeable {

    private final Socket socket;
    private final InputStream in;
    private final OutputStream out;
    private final SwpFrame.Parser parser = new SwpFrame.Parser();

    public SwpLinkClient(String host, int port, int timeoutMillis) throws IOException {
        socket = new Socket();
        socket.connect(new InetSocketAddress(host, port), timeoutMillis);
        socket.setTcpNoDelay(true);
        socket.setSoTimeout(timeoutMillis);
        in = socket.getInputStream();
        out = socket.getOutputStream();
    }

    /** One write for the whole frame, so it leaves as a single segment where it can. */
    public void send(SwpFrame frame) throws IOException {
        byte[] encoded = frame.encode();
        out.write(encoded);
        out.flush();
    }

    public void sendRaw(byte[] data) throws IOException {
        out.write(data);
        out.flush();
    }

    /**
     * Read until a complete frame arrives.
     *
     * @param timeoutMillis overall budget; the socket's own timeout bounds each read.
     * @return the frame, or null if the budget ran out or the peer closed.
     */
    public SwpFrame receive(int timeoutMillis) throws IOException {
        long deadline = System.currentTimeMillis() + timeoutMillis;
        byte[] chunk = new byte[256];

        while (System.currentTimeMillis() < deadline) {
            int read;
            try {
                read = in.read(chunk);
            } catch (SocketTimeoutException e) {
                continue;   // nothing yet; the deadline above is what ends this
            }
            if (read < 0) {
                return null;   // peer closed
            }
            for (int i = 0; i < read; i++) {
                SwpFrame frame = parser.feed(chunk[i]);
                if (frame != null) {
                    return frame;
                }
            }
        }
        return null;
    }

    @Override
    public void close() throws IOException {
        socket.close();
    }
}
