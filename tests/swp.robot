*** Settings ***
Documentation       ETSI TS 102 613 SWP master/slave link: interface states,
...                 the four transitions, the P1..P7 timings that govern them,
...                 and the controller's raw TCP data path.
...
...                 Nothing here asserts on protocol content — these peripherals
...                 carry opaque bytes, so the tests check link behaviour and
...                 byte-for-byte transport, not framing.

Suite Setup         Setup
Suite Teardown      Teardown
Test Setup          Reset Emulation
Test Teardown       Test Teardown
Resource            ${RENODEKEYWORDS}

*** Variables ***
${REPO}                     ${CURDIR}/..
${LINK_PORT}                3460
${UART_UNUSED}              sysbus.swp

# Timings used by the tests, in microseconds. Deliberately far apart so a
# missed deadline is unambiguous rather than a near-miss.
${P1}                       20000
${P2}                       2000
${P3}                       500
${P4}                       1000
${P5}                       1000
${P6}                       10000
${P7}                       20000

*** Keywords ***
Load SWP Sources
    Execute Command         include @${REPO}/peripherals/SWP.cs
    Execute Command         include @${REPO}/peripherals/SWPController.cs
    Execute Command         include @${REPO}/peripherals/SWPSlave.cs

Create SWP Machine
    [Documentation]         A bare link with no CPU: the peripherals are
    ...                     independent of any platform, which is the point.
    [Arguments]             ${port}=0  ${slave_activate_us}=200  ${slave_resume_us}=50
    Create Log Tester       5
    Execute Command         mach create "board"
    Load SWP Sources
    Execute Command         machine LoadPlatformDescriptionFromString "swp: SWP.SWPController @ sysbus { port: ${port}; P1: ${P1}; P2: ${P2}; P3: ${P3}; P4: ${P4}; P5: ${P5}; P6: ${P6}; P7: ${P7} }"
    Execute Command         machine LoadPlatformDescriptionFromString "uicc: SWP.SWPSlave @ swp { P1: ${P1}; P2: ${P2}; P3: ${P3}; P4: ${P4}; P5: ${P5}; P6: ${P6}; P7: ${P7} }"
    # Monitor arguments are passed verbatim, so anything arithmetic has to be
    # reduced to a number on this side first.
    ${activate_us}=         Evaluate  int(${slave_activate_us})
    ${resume_us}=           Evaluate  int(${slave_resume_us})
    Execute Command         sysbus.swp.uicc ActivationResponseTimeMicroseconds ${activate_us}
    Execute Command         sysbus.swp.uicc ResumeResponseTimeMicroseconds ${resume_us}

Link State Should Be
    [Documentation]         Both ends must agree — the slave mirrors the master
    ...                     once a transition has settled.
    [Arguments]             ${expected}
    ${master}=              Execute Command  sysbus.swp LinkState
    ${slave}=               Execute Command  sysbus.swp.uicc LinkState
    Should Be Equal         ${master.strip()}  ${expected}   msg=master state
    Should Be Equal         ${slave.strip()}   ${expected}   msg=slave state

Master State Should Be
    [Arguments]             ${expected}
    ${state}=               Execute Command  sysbus.swp LinkState
    Should Be Equal         ${state.strip()}  ${expected}

Slave Last Received Should Be
    [Arguments]             ${expected}
    ${got}=                 Execute Command  sysbus.swp.uicc LastReceived
    Should Be Equal         ${got.strip()}  ${expected}

Run For Microseconds
    [Documentation]         Advance virtual time deterministically. Every timing
    ...                     assertion below is anchored on this, never on a
    ...                     host-side sleep.
    [Arguments]             ${us}
    # The parentheses are load-bearing: ${us} is substituted as an expression,
    # so "1000 + 1000 / 1000000.0" would divide only the last term and run a
    # thousand seconds of virtual time instead of two milliseconds.
    ${seconds}=             Evaluate  (${us}) / 1000000.0
    Execute Command         emulation RunFor "${seconds}"

Bring Link Up
    [Documentation]         Runs only as long as the activation actually needs
    ...                     (P5 plus the slave's answer). Running the full P6
    ...                     worst case would spend P1 of idle time and trip the
    ...                     auto-suspend on the way up.
    [Arguments]             ${settle_us}=1000
    Execute Command         sysbus.swp Activate
    Run For Microseconds    ${P5} + ${settle_us}
    Link State Should Be    Activated

*** Test Cases ***

# ------------------------------------------------------------------
#  States
# ------------------------------------------------------------------

Link Starts Deactivated
    [Documentation]         Before anything drives the wire, S1 is low and both
    ...                     ends read DEACTIVATED.
    Create SWP Machine
    Link State Should Be    Deactivated

# ------------------------------------------------------------------
#  ACTIVATE
# ------------------------------------------------------------------

Activate Brings The Link Up
    Create SWP Machine
    Execute Command         sysbus.swp AutoSuspend false
    Bring Link Up

Activate Addresses The Slave Only After P5
    [Documentation]         The master must let the line settle for P5 before
    ...                     addressing the slave, so the link cannot be up
    ...                     earlier than that no matter how fast the slave is.
    Create SWP Machine      slave_activate_us=1
    Execute Command         sysbus.swp AutoSuspend false
    Execute Command         sysbus.swp Activate

    # Half of P5 in: the slave has not been addressed yet.
    Run For Microseconds    ${P5} / 2
    Master State Should Be  Deactivated

    Run For Microseconds    ${P5}
    Link State Should Be    Activated

Activation Fails When The Slave Misses P6
    [Documentation]         A slave that answers later than P6 leaves the link
    ...                     DEACTIVATED rather than coming up late.
    Create SWP Machine      slave_activate_us=${P6} * 2
    Execute Command         sysbus.swp Activate

    Run For Microseconds    ${P5} + ${P6} + 1000
    Master State Should Be  Deactivated
    Wait For Log Entry      ACTIVATE failed  timeout=1  treatAsRegex=false

Activation Fails With No Slave Registered
    [Documentation]         Nothing to activate against — the master must not
    ...                     report a link that is not there.
    Execute Command         mach create "board"
    Load SWP Sources
    Execute Command         machine LoadPlatformDescriptionFromString "swp: SWP.SWPController @ sysbus { port: 0 }"
    Execute Command         sysbus.swp Activate
    Run For Microseconds    50000
    Master State Should Be  Deactivated

# ------------------------------------------------------------------
#  SUSPEND / RESUME
# ------------------------------------------------------------------

Link Suspends By Itself After P1 Of Idle Bits
    [Documentation]       With AutoSuspend on, P1 of nothing-but-idle-bits is
    ...                     the master's cue to stop the bit clock.
    Create SWP Machine
    Bring Link Up
    Run For Microseconds    ${P1} * 2
    Link State Should Be    Suspended

Link Stays Activated Past P1 When AutoSuspend Is Off
    Create SWP Machine
    Execute Command         sysbus.swp AutoSuspend false
    Bring Link Up
    Run For Microseconds    ${P1} * 4
    Link State Should Be    Activated

Explicit Suspend And Resume Round Trip
    Create SWP Machine
    Execute Command         sysbus.swp AutoSuspend false
    Bring Link Up

    Execute Command         sysbus.swp Suspend
    Run For Microseconds    100
    Link State Should Be    Suspended

    Execute Command         sysbus.swp Resume
    Run For Microseconds    ${P2} + ${P3}
    Link State Should Be    Activated

Resume Reaches Activated Only At The End Of The P2 Idle Bits
    [Documentation]       The spec puts the transition at the end of the last
    ...                     idle bit, not at the start of the sequence.
    Create SWP Machine      slave_resume_us=1
    Execute Command         sysbus.swp AutoSuspend false
    Bring Link Up
    Execute Command         sysbus.swp Suspend
    Run For Microseconds    100
    Link State Should Be    Suspended

    Execute Command         sysbus.swp Resume
    Run For Microseconds    ${P2} / 2
    Master State Should Be  Suspended

    Run For Microseconds    ${P2}
    Master State Should Be  Activated

Resume Reports A Slave That Misses P3max
    [Documentation]       The link still comes up — the master owns that — but
    ...                     the missed transition sequence must be reported.
    Create SWP Machine      slave_resume_us=${P3} * 4
    Execute Command         sysbus.swp AutoSuspend false
    Bring Link Up
    Execute Command         sysbus.swp Suspend
    Run For Microseconds    100

    Execute Command         sysbus.swp Resume
    Run For Microseconds    ${P2} + ${P3} + 100
    Wait For Log Entry      P3max  timeout=1  treatAsRegex=false

Resume Is Refused On A Deactivated Link
    [Documentation]       There is no bit clock to restart — the master must
    ...                     ACTIVATE, not RESUME.
    Create SWP Machine
    Link State Should Be    Deactivated
    Execute Command         sysbus.swp Resume
    Run For Microseconds    ${P2} * 4
    Master State Should Be  Deactivated

Slave Can Ask For A Resume While Suspended
    [Documentation]       On the wire the UICC modulates S2 to wake a suspended
    ...                     link; that is how a card-emulation event gets out.
    Create SWP Machine
    Execute Command         sysbus.swp AutoSuspend false
    Bring Link Up
    Execute Command         sysbus.swp Suspend
    Run For Microseconds    100
    Link State Should Be    Suspended

    Execute Command         sysbus.swp.uicc Send "6F 1A"
    Run For Microseconds    ${P2} + ${P3} + 1000
    Link State Should Be    Activated

# ------------------------------------------------------------------
#  DEACTIVATE
# ------------------------------------------------------------------

Deactivate Takes Effect Only After P4
    [Documentation]       SWIO has to be held low for longer than P4 before the
    ...                     link is observably DEACTIVATED.
    Create SWP Machine
    Execute Command         sysbus.swp AutoSuspend false
    Bring Link Up

    Execute Command         sysbus.swp Deactivate
    Run For Microseconds    ${P4} / 2
    Master State Should Be  Activated

    Run For Microseconds    ${P4}
    Link State Should Be    Deactivated

Deactivate Works From Suspended
    Create SWP Machine
    Bring Link Up
    Run For Microseconds    ${P1} * 2
    Link State Should Be    Suspended

    Execute Command         sysbus.swp Deactivate
    Run For Microseconds    ${P4} * 2
    Link State Should Be    Deactivated

Link Can Be Reactivated After Deactivation
    Create SWP Machine
    Execute Command         sysbus.swp AutoSuspend false
    Bring Link Up
    Execute Command         sysbus.swp Deactivate
    Run For Microseconds    ${P4} * 2
    Link State Should Be    Deactivated

    Execute Command         sysbus.swp Activate
    Run For Microseconds    ${P5} + ${P6}
    Link State Should Be    Activated

# ------------------------------------------------------------------
#  Data transport — raw bytes, no framing
# ------------------------------------------------------------------

Master Data Reaches The Slave Byte For Byte
    Create SWP Machine
    Execute Command         sysbus.swp AutoSuspend false
    Bring Link Up

    Execute Command         sysbus.swp Transmit "00 A4 04 00 07"
    Run For Microseconds    1000
    Slave Last Received Should Be  00 A4 04 00 07

Payload Bytes Are Never Interpreted
    [Documentation]       0xFF, 0x00 and would-be SOF/EOF patterns must survive
    ...                     unchanged: framing is the client's business, and the
    ...                     socket runs with Telnet mode off so 0xFF is not
    ...                     escaped on the way out.
    Create SWP Machine
    Execute Command         sysbus.swp AutoSuspend false
    Bring Link Up

    Execute Command         sysbus.swp Transmit "FF 00 F0 FF FF 7E 0F"
    Run For Microseconds    1000
    Slave Last Received Should Be  FF 00 F0 FF FF 7E 0F

Slave Data Reaches The Master
    Create SWP Machine
    Execute Command         sysbus.swp AutoSuspend false
    Bring Link Up

    Execute Command         sysbus.swp.uicc Send "6F 1A 84 07"
    Run For Microseconds    1000
    ${count}=               Execute Command  sysbus.swp BytesFromSlave
    Should Be Equal As Integers  ${count.strip()}  4

Both Directions Carry Traffic At Once
    [Documentation]       Full duplex is the whole point of SWP: voltage one
    ...                     way, current the other, simultaneously. Neither
    ...                     direction may wait on the other.
    Create SWP Machine
    Execute Command         sysbus.swp AutoSuspend false
    Bring Link Up

    Execute Command         sysbus.swp Transmit "01 02 03"
    Execute Command         sysbus.swp.uicc Send "AA BB"
    Run For Microseconds    1000

    Slave Last Received Should Be  01 02 03
    ${to_slave}=            Execute Command  sysbus.swp BytesToSlave
    ${from_slave}=          Execute Command  sysbus.swp BytesFromSlave
    Should Be Equal As Integers  ${to_slave.strip()}    3
    Should Be Equal As Integers  ${from_slave.strip()}  2

Transmitting On A Suspended Link Resumes It First
    Create SWP Machine
    Execute Command         sysbus.swp AutoSuspend false
    Bring Link Up
    Execute Command         sysbus.swp Suspend
    Run For Microseconds    100
    Link State Should Be    Suspended

    Execute Command         sysbus.swp Transmit "11 22 33"
    Run For Microseconds    ${P2} + ${P3} + 1000
    Link State Should Be    Activated
    Slave Last Received Should Be  11 22 33

Transmitting On A Deactivated Link Drops The Data
    [Documentation]       There is no wire to put the bytes on, so they are
    ...                     dropped and counted rather than silently queued.
    Create SWP Machine
    Link State Should Be    Deactivated

    Execute Command         sysbus.swp Transmit "11 22 33"
    Run For Microseconds    1000
    ${dropped}=             Execute Command  sysbus.swp Overruns
    Should Be True          ${dropped.strip()} > 0

Loopback Returns A Full Round Trip
    Create SWP Machine
    Execute Command         sysbus.swp AutoSuspend false
    Execute Command         sysbus.swp.uicc Loopback true
    Bring Link Up

    Execute Command         sysbus.swp Transmit "DE AD BE EF"
    Run For Microseconds    2000
    ${from_slave}=          Execute Command  sysbus.swp BytesFromSlave
    Should Be Equal As Integers  ${from_slave.strip()}  4

# ------------------------------------------------------------------
#  Configurability — the peripherals must be agnostic
# ------------------------------------------------------------------

Timings Are Configurable At Runtime
    [Documentation]       Every P value is read when its deadline is armed, so
    ...                     changing one takes effect without reloading anything.
    Create SWP Machine
    Execute Command         sysbus.swp P5 5000
    ${p5}=                  Execute Command  sysbus.swp P5
    Should Be Equal As Integers  ${p5.strip()}  5000

    Execute Command         sysbus.swp AutoSuspend false
    Execute Command         sysbus.swp Activate
    Run For Microseconds    2000
    Master State Should Be  Deactivated

    Run For Microseconds    5000
    Master State Should Be  Activated

Reset Returns The Link To Deactivated
    Create SWP Machine
    Execute Command         sysbus.swp AutoSuspend false
    Bring Link Up

    Execute Command         sysbus.swp Reset
    Execute Command         sysbus.swp.uicc Reset
    Link State Should Be    Deactivated

# ------------------------------------------------------------------
#  The TCP data path
# ------------------------------------------------------------------

Controller Opens Its TCP Port
    Create SWP Machine      port=${LINK_PORT}
    ${status}=              Execute Command  sysbus.swp Status
    Should Contain          ${status}  ${LINK_PORT}
