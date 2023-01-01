interface I1<T>
{
  void M(int parameter)
  {
    // display class is optimized into struct
    void Local() => parameter++;
    Local();
  }
}

interface I2<out T>
{
  void M(int parameter)
  {
    // display class is a reference type
    void Local() => parameter++;
    Local();
  }
}