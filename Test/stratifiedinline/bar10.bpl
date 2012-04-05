var i: int;
var m: int;

procedure {:inline 1} foo()
modifies i;
{
  if (i < 20) {
      i := i + 1;
      call foo();
  }
}

procedure {:inline 1} bar1(j: int) 
modifies i;
{
  if (j < 2*m) 
  {
    i := i + 1;
    call bar1(j+1);
  }
}

procedure {:inline 1} bar2(j: int) 
modifies i;
{
  if (j < m) {
    i := i - 1;
    call bar2(j+1);
  }
}

procedure main() 
modifies i;
{
  i := 0;
  call bar1(0);
  call bar2(0);
  i := 0;
  call foo();
  assert i < 10;
}