*** Settings ***
Documentation       The NUCLEO-H533RE example: real firmware on an STM32H533RE
...                 playing the UICC end of an SWP link, driven from the CLF
...                 end. Proves the whole stack together — link state machine,
...                 P timings, the memory-mapped register front-end, and the
...                 firmware's own framing on top of a link that carries only
...                 opaque bytes.

Suite Setup         Setup
Suite Teardown      Teardown
Test Setup          Reset Emulation
Test Teardown       Test Teardown
Resource            ${RENODEKEYWORDS}

*** Variables ***
${REPO}                     ${CURDIR}/..
${ELF}                      ${CURDIR}/../firmware/build/swp-demo.elf
${UART}                     sysbus.usart3

# A frame the firmware will accept: SOF | LEN | 01 02 03 | CRC8 | EOF.
${REQUEST}                  7E 03 01 02 03 48 7F

# 0xFF and an embedded SOF/EOF must survive: the link never inspects payload,
# and the socket runs with Telnet mode off so 0xFF is not escaped.
${TRICKY_REQUEST}           7E 05 FF 00 FF 7E 7F 1C 7F

*** Keywords ***
Create Nucleo Machine
    Execute Command         mach create "nucleo_h533re"
    Execute Command         include @${REPO}/peripherals/SWP.cs
    Execute Command         include @${REPO}/peripherals/SWPController.cs
    Execute Command         include @${REPO}/peripherals/SWPSlave.cs
    Execute Command         include @${REPO}/peripherals/SWPRegisterInterface.cs
    Execute Command         include @${REPO}/peripherals/ChipEventController.cs
    Execute Command         include @${REPO}/peripherals/SWPChipEventController.cs

    # The shipped platform file, so the test exercises the example itself
    # rather than a re-description of it that could drift from it.
    Execute Command         machine LoadPlatformDescription @${REPO}/platforms/nucleo_h533re.repl

    Execute Command         sysbus LoadELF @${ELF}
    Create Terminal Tester  ${UART}

Link State Should Be
    [Arguments]             ${expected}
    ${master}=              Execute Command  sysbus.swp LinkState
    ${slave}=               Execute Command  sysbus.swp.uicc LinkState
    Should Be Equal         ${master.strip()}  ${expected}   msg=master state
    Should Be Equal         ${slave.strip()}   ${expected}   msg=slave state

*** Test Cases ***

Firmware Boots And Sees A Deactivated Link
    Create Nucleo Machine
    Start Emulation
    Wait For Line On Uart   NUCLEO-H533RE SWP slave demo
    Wait For Line On Uart   link DEACTIVATED

Firmware Observes The Link Coming Up
    [Documentation]         The firmware reads the link state out of SR, so this
    ...                     exercises the register front-end as well as the link.
    Create Nucleo Machine
    Start Emulation
    Wait For Line On Uart   link DEACTIVATED

    Execute Command         sysbus.swp Activate
    Wait For Line On Uart   link ACTIVATED
    Link State Should Be    Activated

Firmware Receives A Frame And Answers It
    [Documentation]         The link carries opaque bytes; the framing is the
    ...                     firmware's at this end and the client's at the other.
    Create Nucleo Machine
    Execute Command         sysbus.swp AutoSuspend false
    Start Emulation
    Execute Command         sysbus.swp Activate
    Wait For Line On Uart   link ACTIVATED

    Execute Command         sysbus.swp Transmit "${REQUEST}"
    Wait For Line On Uart   RX frame: 01 02 03
    Wait For Line On Uart   TX reply sent

    # 7 bytes each way: SOF, LEN, three payload bytes, CRC, EOF.
    ${sent}=                Execute Command  sysbus.swp BytesToSlave
    ${received}=            Execute Command  sysbus.swp BytesFromSlave
    Should Be Equal As Integers  ${sent.strip()}      7
    Should Be Equal As Integers  ${received.strip()}  7

Payload Bytes Survive The Link Untouched
    [Documentation]         0xFF, 0x00 and bytes that look like SOF/EOF must
    ...                     reach the firmware unchanged.
    Create Nucleo Machine
    Execute Command         sysbus.swp AutoSuspend false
    Start Emulation
    Execute Command         sysbus.swp Activate
    Wait For Line On Uart   link ACTIVATED

    Execute Command         sysbus.swp Transmit "${TRICKY_REQUEST}"
    Wait For Line On Uart   RX frame: FF 00 FF 7E 7F
    Wait For Line On Uart   TX reply sent

Firmware Sees The Link Suspend And Resume
    Create Nucleo Machine
    Start Emulation
    Execute Command         sysbus.swp Activate
    Wait For Line On Uart   link ACTIVATED

    Execute Command         sysbus.swp Suspend
    Wait For Line On Uart   link SUSPENDED

    Execute Command         sysbus.swp Resume
    Wait For Line On Uart   link ACTIVATED

Firmware Sees The Link Deactivate
    Create Nucleo Machine
    Execute Command         sysbus.swp AutoSuspend false
    Start Emulation
    Execute Command         sysbus.swp Activate
    Wait For Line On Uart   link ACTIVATED

    Execute Command         sysbus.swp Deactivate
    Wait For Line On Uart   link DEACTIVATED
    Link State Should Be    Deactivated

Chip Event Opcodes Drive The Link
    [Documentation]         Opcode 0x07/0x08 through the ChipEventController,
    ...                     injected the same way a socket client would.
    Create Nucleo Machine
    Execute Command         sysbus.swp AutoSuspend false
    Start Emulation
    Wait For Line On Uart   link DEACTIVATED

    Execute Command         sysbus.chipEvents Inject 0x07
    Wait For Line On Uart   link ACTIVATED

    Execute Command         sysbus.chipEvents Inject 0x08
    Wait For Line On Uart   link DEACTIVATED
