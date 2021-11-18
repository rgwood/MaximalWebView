namespace MaximalWebView;

public class HostObject
{
    public int MyNumber { get; set; } = 42;

    public int Increment() => ++MyNumber;

    public int Add1(int x) => x + 1;
}
