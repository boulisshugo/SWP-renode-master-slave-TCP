/*
 * Minimal Cortex-M33 startup for the NUCLEO-H533RE SWP demo.
 *
 * Just enough to reach main(): a vector table with the initial stack pointer
 * and reset handler, .data copied out of flash and .bss zeroed. No clock tree
 * setup — Renode does not model the PLL, and the demo's timing comes from the
 * SWP link, not from the CPU frequency.
 */

#include <stdint.h>

extern uint32_t _sidata, _sdata, _edata, _sbss, _ebss, _estack;

int main(void);

void Reset_Handler(void)
{
    uint32_t *src = &_sidata;
    uint32_t *dst = &_sdata;

    while(dst < &_edata) {
        *dst++ = *src++;
    }
    for(dst = &_sbss; dst < &_ebss; dst++) {
        *dst = 0;
    }

    main();

    for(;;) {
    }
}

void Default_Handler(void)
{
    for(;;) {
    }
}

/* The SWP register interface is wired to NVIC line 103 in the .repl. The demo
 * polls rather than servicing the interrupt, so the default handler is enough
 * to prove the line is wired without changing the control flow. */
void SWPMI_IRQHandler(void) __attribute__((weak, alias("Default_Handler")));

__attribute__((section(".isr_vector"), used))
void (* const vector_table[])(void) = {
    (void (*)(void))&_estack,
    Reset_Handler,
    Default_Handler,   /* NMI */
    Default_Handler,   /* HardFault */
    Default_Handler,   /* MemManage */
    Default_Handler,   /* BusFault */
    Default_Handler,   /* UsageFault */
};
