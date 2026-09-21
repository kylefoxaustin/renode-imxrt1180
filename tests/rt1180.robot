*** Comments ***
RT1180 Renode test suite.
Every oracle below is the string the FIRMWARE ITSELF prints. The NXP SDK oracles
are taken verbatim from the QEMU model's scorecard (docs/validation/corpus.tsv)
so both tools are judged by the identical criterion; the Zephyr oracles are the
samples' own banners.

Binaries come from the pinned artifact store ~/.cache/rt1180-artifacts, with
sha256s in MANIFEST.sha256 -- so this suite and any future QEMU run provably
execute the SAME bytes.

Zephyr's XIP images carry a boot header BEFORE the vector table, so VTOR/SP/PC
are supplied explicitly; the values are read from the ELF (nm/objdump) and are
recorded in the variables below rather than guessed.

*** Variables ***
${PROJ}      /home/kyle/Documents/GitHub/rt1180renode
${ART}       /home/kyle/.cache/rt1180-artifacts
${UART33}    sysbus.lpuart1
${UART7}     sysbus.lpuart12

*** Keywords ***
Load Peripherals
    Execute Command    include @${PROJ}/scripts/load_peripherals.resc

Create Machine
    [Arguments]    ${repl}    ${elf}    ${mips}=240
    Execute Command    mach create
    Load Peripherals
    Execute Command    machine LoadPlatformDescription @${PROJ}/platforms/${repl}
    # Blanket TrustZone-M secure mirror: the whole NS peripheral window at +0x1000_0000.
    Execute Command    sysbus Redirect 0x50000000 0x40000000 0x10000000
    Execute Command    cpu PerformanceInMips ${mips}
    Execute Command    sysbus LoadELF @${elf}

Create Zephyr Machine
    [Arguments]    ${repl}    ${elf}    ${vt}    ${sp}    ${pc}    ${mips}=240
    Create Machine    ${repl}    ${elf}    ${mips}
    Execute Command    cpu VectorTableOffset ${vt}
    Execute Command    cpu SP ${sp}
    Execute Command    cpu PC ${pc}

*** Test Cases ***
# ─────────────────────────── M0 ───────────────────────────
Should Run SDK hello_world On CM33
    Create Machine           mimxrt1189_cm33.repl    ${ART}/sdk/hello_world_cm33.elf
    Create Terminal Tester   ${UART33}
    Start Emulation
    Wait For Line On Uart    hello world.

# ─────────────────────────── M1 ───────────────────────────
Should Run SDK eDMA4 Memory To Memory
    Create Machine           mimxrt1189_m1.repl    ${ART}/sdk/edma4_memory_to_memory_cm33.elf
    Create Terminal Tester   ${UART33}
    Start Emulation
    Wait For Line On Uart    EDMA memory to memory example finish    timeout=10

Should Run SDK eDMA4 Scatter Gather
    Create Machine           mimxrt1189_m1.repl    ${ART}/sdk/edma4_scatter_gather_cm33.elf
    Create Terminal Tester   ${UART33}
    Start Emulation
    Wait For Line On Uart    EDMA scatter gather transfer example finish    timeout=10

Should Run SDK S3MU Example
    Create Machine           mimxrt1189_m1.repl    ${ART}/sdk/s3mu_cm33.elf
    Create Terminal Tester   ${UART33}
    Start Emulation
    Wait For Line On Uart    End of Example with SUCCESS!!    timeout=10

# ─────────────────────────── M2 ───────────────────────────
Should Boot The Secondary Core
    Create Machine           mimxrt1189_m2.repl    ${ART}/sdk/multicore_manager_primary_core_cm33.elf
    Execute Command          cpu1 IsHalted true
    Execute Command          cpu1 PerformanceInMips 798
    Create Terminal Tester   ${UART33}
    Start Emulation
    Wait For Line On Uart    The secondary core application has been started.    timeout=30

Should Run RPMsg Ping Pong Between Cores
    Create Machine           mimxrt1189_m2.repl    ${ART}/sdk/rpmsg_lite_pingpong_primary_core_cm33.elf
    Execute Command          cpu1 IsHalted true
    Execute Command          cpu1 PerformanceInMips 798
    Create Terminal Tester   ${UART33}
    Start Emulation
    # The ORACLE is the terminal DATA value, deliberately NOT the "RPMsg demo ends"
    # banner -- the failure form of this example contains that banner too.
    Wait For Line On Uart    Message: Size=4, DATA = 101    timeout=30

# ───────────────────────── Zephyr ─────────────────────────
Should Run Zephyr hello_world On CM33
    Create Zephyr Machine    mimxrt1189_zephyr.repl    ${ART}/zephyr/cm33-hello_world.elf
    ...                      0x3800b000    0x140006D0    0x3800CBA5
    Create Terminal Tester   ${UART33}
    Start Emulation
    Wait For Line On Uart    Hello World! mimxrt1180_evk    timeout=15

Should Run Zephyr synchronization On CM33
    Create Zephyr Machine    mimxrt1189_zephyr.repl    ${ART}/zephyr/cm33-synchronization.elf
    ...                      0x3800b000    0x14001090    0x3800CC75
    Create Terminal Tester   ${UART33}
    Start Emulation
    Wait For Line On Uart    thread_a: Hello World    timeout=15
    Wait For Line On Uart    thread_b: Hello World    timeout=15

Should Run Zephyr hello_world On CM7
    Create Zephyr Machine    mimxrt1189_cm7.repl    ${ART}/zephyr/cm7-hello_world.elf
    ...                      0x0    0x20000700    0x1B35    798
    Create Terminal Tester   ${UART7}
    Start Emulation
    Wait For Line On Uart    Hello World! mimxrt1180_evk    timeout=15

Should Run Zephyr synchronization On CM7
    Create Zephyr Machine    mimxrt1189_cm7.repl    ${ART}/zephyr/cm7-synchronization.elf
    ...                      0x0    0x20001140    0x1C05    798
    Create Terminal Tester   ${UART7}
    Start Emulation
    Wait For Line On Uart    thread_a: Hello World    timeout=15
    Wait For Line On Uart    thread_b: Hello World    timeout=15
