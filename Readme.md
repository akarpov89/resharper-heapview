ReSharper Heap Allocations Viewer plugin
----------------------------------------

This plugins statically analyzes C# code to find all local object allocations happening.

* It can detect and visualize [all boxing cases](http://stackoverflow.com/questions/7995606/boxing-occurrence-in-c-sharp), including:
```c#
struct Boxing {
  void M(string a) {
    object obj = 42;                // implicit conversion Int32 ~> Object
    string path = a + '/' + obj;    // implicit conversion Char ~> Object
    int code = this.GetHashCode();  // non-overriden virtual method call on struct
    string caseA = E.A.ToString();  // the same, virtual call
    IComparable comparable = E.A;   // valuetype conversion to interface type
    Action<string> action = this.M; // delegate from value type method
    Type type = this.GetType();     // GetType() call is always virtual
  }

  enum E { A, B, C }
}
```
* It can visualize some hidden allocations happening in C#, including:
```c#
class HeapAllocations {
  List<int> _xs = new List<int>();  // explicit object creation expressions
  int[] _ys = {1, 2, 3};            // allocation via array initializer syntax

  void M(params string[] args) {
    string c = args[0] + "/";       // string concatenation
    M("abc", "def");                // parameters array allocation
    M();                            // the same, hidden 'new string[0]'
    var xs = Enumerable.Range(0,1); // iterator method call
    var ys = from x in xs
             let y = x + 1          // anonymous type creation for 'let'
             select x + y;
  }

  void N(List<string> xs) {
    foreach (var s in xs) F(s);     // no allocations, valuetype enumerator

    IEnumerable<string> ys = xs;
    foreach (var s in ys) F(s);     // possible enumerator allocation in foreach
  }
}
```
* It can detect delegate instances allocation, ignoring cached delegates:
```c#
class Delegates {
  static void M(string s) {
    Action<string> method = M;      // non-cached delegate from method group
    Action lambda = () => M("a");   // cached delegate from static lambda
    Action closure = () => M(s);    // non-cached lambda with closure 's'
  }

  void M<T>() {
    Action generic = () => { };     // non-cached, lambda in generic method
    Action closure = () => M<T>();  // non-cached, captures 'this' to closure
  }
}
```
* It can detect closure ('display classes') creation points:
```c#
class Closures {
  IEnumerable<string> StupidExample(string str) {
    // hidden closure class allocation happens here
    if (str == null)
      return Enumerable.Empty<string>();

    if (str.Length == 0) {
      return Enumerable.Empty<string>();
    } else {
      // second hidden closure allocation (nested scope)
      int value = str.Count(x => x == '_');

      return Enumerable
        .Range(0, str.Length)
        .Select(count => // delegate instance allocation
          str.Substring(Math.Max(value, count)));
    }
  }
}
```


Roadmap:

* Review highlighting ranges (parameters, params methods)
* Make highlightings configurable, add ability to search occurances
* Roslyn and C# 6.0 changes support

[Changelog is here](Content/Changelog.md)

