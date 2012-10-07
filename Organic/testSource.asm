    ; .file "fib.c"
    ;.text
    ;.globl  fib
    ; .align    1
:fib
    SUB SP, 0x6
    #include "test.dasm"
    SET PICK 0x5, A
    SET PICK 0x4, 0x1
    SET PICK 0x3, 0x1
    SET PICK 0x1, 0x0
    SET PEEK, A
:.LBB0_1
    SET A, PICK 0x1
    SET B, PICK 0x5
    IFE A, B
    SET PC, .LBB0_4
    IFA A, B
    SET PC, .LBB0_4
    SET PC, .LBB0_2
:.LBB0_2
    SET A, PICK 0x4
    SET B, PICK 0x3
    ADD A, B
    SET PICK 0x2, A
    SET A, PICK 0x4
    SET PICK 0x3, A
    SET A, PICK 0x2
    SET PICK 0x4, A
    SET A, PICK 0x1
    ADD A, 0x1
    SET PICK 0x1, A
    SET PC, .LBB0_1
:.LBB0_4
    SET A, PICK 0x4
    ADD SP, 0x6
    SET PC, POP

    ;.globl  main
    ; .align    1
:main
    SET PUSH, J
    SET J, SP
    SUB SP, 0x1
    SET [J+0xffff], 0x0
    SET A, 0xa
    JSR fib
    ADD SP, 0x1
    SET J, POP
    SET PC, POP