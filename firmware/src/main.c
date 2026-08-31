/*
 * NUCLEO-H533RE SWP demo firmware.
 *
 * The board plays the UICC end of an SWP link. A host TCP client connected to
 * the controller's port is the CLF: it activates the link and writes raw bytes,
 * which arrive here through the SWP register interface.
 *
 * This firmware owns the framing at its end of the wire — the link peripherals
 * carry opaque bytes and never inspect them. The frame shape used here is the
 * SWP one from ETSI TS 102 613 reduced to what a demo needs:
 *
 *     SOF | LEN | PAYLOAD (LEN bytes) | CRC8 | EOF
 *
 * It is deliberately simple rather than spec-exact: bit stuffing and the real
 * CRC-16 live in a UICC stack, not in an example. The point is that the frame
 * is built and checked *here*, above a link that knows nothing about it.
 *
 * On a well-formed frame the firmware answers with a frame of its own whose
 * payload is the request payload with each byte incremented — enough for the
 * host to prove the round trip end to end.
 */

#include <stdint.h>
#include <stddef.h>

/* ---- SWP register interface (see peripherals/SWPRegisterInterface.cs) ---- */

#define SWPMI_BASE      0x40008800UL

#define SWPMI_CR        (*(volatile uint32_t *)(SWPMI_BASE + 0x00))
#define SWPMI_SR        (*(volatile uint32_t *)(SWPMI_BASE + 0x04))
#define SWPMI_TDR       (*(volatile uint32_t *)(SWPMI_BASE + 0x08))
#define SWPMI_RDR       (*(volatile uint32_t *)(SWPMI_BASE + 0x0C))
#define SWPMI_RXLEVEL   (*(volatile uint32_t *)(SWPMI_BASE + 0x10))

#define CR_ACTIVATE     (1U << 0)
#define CR_DEACTIVATE   (1U << 1)
#define CR_SUSPEND      (1U << 2)
#define CR_RESUME       (1U << 3)
#define CR_RXNEIE       (1U << 8)
#define CR_LINKIE       (1U << 9)

#define SR_STATE_MASK   (3U << 0)
#define SR_RXNE         (1U << 2)
#define SR_TXE          (1U << 3)
#define SR_LINKCH       (1U << 4)
#define SR_OVR          (1U << 5)

#define LINK_DEACTIVATED  0U
#define LINK_SUSPENDED    1U
#define LINK_ACTIVATED    2U

/* ---- USART3: the ST-LINK virtual COM port on a NUCLEO-H533RE ---- */

#define USART3_BASE     0x40004800UL
#define USART3_CR1      (*(volatile uint32_t *)(USART3_BASE + 0x00))
#define USART3_ISR      (*(volatile uint32_t *)(USART3_BASE + 0x1C))
#define USART3_TDR      (*(volatile uint32_t *)(USART3_BASE + 0x28))

#define USART_CR1_UE    (1U << 0)
#define USART_CR1_TE    (1U << 3)
#define USART_ISR_TXE   (1U << 7)

static void uart_init(void)
{
    USART3_CR1 = USART_CR1_UE | USART_CR1_TE;
}

static void uart_putc(char c)
{
    while(!(USART3_ISR & USART_ISR_TXE)) {
    }
    USART3_TDR = (uint32_t)(unsigned char)c;
}

static void uart_puts(const char *s)
{
    while(*s) {
        uart_putc(*s++);
    }
}

static void uart_puthex(uint8_t v)
{
    static const char digits[] = "0123456789ABCDEF";
    uart_putc(digits[(v >> 4) & 0xF]);
    uart_putc(digits[v & 0xF]);
}

/* ---- framing, owned entirely by this firmware ---- */

#define SOF             0x7EU
#define EOF_BYTE        0x7FU
#define MAX_PAYLOAD     64U

static uint8_t crc8(const uint8_t *data, uint32_t len)
{
    /* CRC-8 with polynomial 0x07, the usual smart-card choice. */
    uint8_t crc = 0x00;
    for(uint32_t i = 0; i < len; i++) {
        crc ^= data[i];
        for(int bit = 0; bit < 8; bit++) {
            crc = (crc & 0x80) ? (uint8_t)((crc << 1) ^ 0x07) : (uint8_t)(crc << 1);
        }
    }
    return crc;
}

static void swp_send(const uint8_t *data, uint32_t len)
{
    for(uint32_t i = 0; i < len; i++) {
        while(!(SWPMI_SR & SR_TXE)) {
        }
        SWPMI_TDR = data[i];
    }
}

static void send_frame(const uint8_t *payload, uint8_t len)
{
    uint8_t header[2];
    uint8_t trailer[2];

    header[0] = SOF;
    header[1] = len;
    trailer[0] = crc8(payload, len);
    trailer[1] = EOF_BYTE;

    swp_send(header, 2);
    swp_send(payload, len);
    swp_send(trailer, 2);
}

/* ---- receive state machine ---- */

enum rx_state {
    WAIT_SOF,
    WAIT_LEN,
    WAIT_PAYLOAD,
    WAIT_CRC,
    WAIT_EOF
};

static enum rx_state state = WAIT_SOF;
static uint8_t payload[MAX_PAYLOAD];
static uint8_t expected_len;
static uint8_t received_len;
static uint8_t received_crc;

static void handle_frame(void)
{
    uint8_t reply[MAX_PAYLOAD];

    uart_puts("RX frame:");
    for(uint8_t i = 0; i < received_len; i++) {
        uart_putc(' ');
        uart_puthex(payload[i]);
    }
    uart_puts("\r\n");

    for(uint8_t i = 0; i < received_len; i++) {
        reply[i] = (uint8_t)(payload[i] + 1U);
    }

    send_frame(reply, received_len);
    uart_puts("TX reply sent\r\n");
}

static void feed(uint8_t byte)
{
    switch(state) {
    case WAIT_SOF:
        if(byte == SOF) {
            state = WAIT_LEN;
        }
        break;

    case WAIT_LEN:
        if(byte == 0 || byte > MAX_PAYLOAD) {
            uart_puts("bad length, resyncing\r\n");
            state = WAIT_SOF;
            break;
        }
        expected_len = byte;
        received_len = 0;
        state = WAIT_PAYLOAD;
        break;

    case WAIT_PAYLOAD:
        payload[received_len++] = byte;
        if(received_len == expected_len) {
            state = WAIT_CRC;
        }
        break;

    case WAIT_CRC:
        received_crc = byte;
        state = WAIT_EOF;
        break;

    case WAIT_EOF:
        if(byte != EOF_BYTE) {
            uart_puts("missing EOF, dropping frame\r\n");
        } else if(received_crc != crc8(payload, received_len)) {
            uart_puts("CRC mismatch, dropping frame\r\n");
        } else {
            handle_frame();
        }
        state = WAIT_SOF;
        break;
    }
}

/* ---- main ---- */

int main(void)
{
    uint32_t last_state = 0xFFFFFFFFU;

    uart_init();
    uart_puts("\r\nNUCLEO-H533RE SWP slave demo\r\n");
    uart_puts("waiting for the host to activate the link\r\n");

    /* Polled, not interrupt driven: it keeps the example to one file and the
     * link's own timing is what this demo is about. LINKIE/RXNEIE are enabled
     * anyway so the IRQ wiring is exercised. */
    SWPMI_CR = CR_RXNEIE | CR_LINKIE;

    for(;;) {
        uint32_t sr = SWPMI_SR;

        if(sr & SR_LINKCH) {
            SWPMI_SR = SR_LINKCH;   /* write 1 to clear */
        }

        uint32_t link = sr & SR_STATE_MASK;
        if(link != last_state) {
            last_state = link;
            switch(link) {
            case LINK_DEACTIVATED:
                uart_puts("link DEACTIVATED\r\n");
                state = WAIT_SOF;
                break;
            case LINK_SUSPENDED:
                uart_puts("link SUSPENDED\r\n");
                break;
            case LINK_ACTIVATED:
                uart_puts("link ACTIVATED\r\n");
                break;
            default:
                break;
            }
        }

        if(sr & SR_OVR) {
            SWPMI_SR = SR_OVR;
            uart_puts("RX overrun\r\n");
        }

        while(SWPMI_SR & SR_RXNE) {
            feed((uint8_t)(SWPMI_RDR & 0xFF));
        }
    }
}
