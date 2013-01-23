  set pc, main
:tmp
  .dat 0x0
:main
  set [tmp], 0xa
  set a, 0x0
  set a, [tmp]
  set a, 0x1
:exit