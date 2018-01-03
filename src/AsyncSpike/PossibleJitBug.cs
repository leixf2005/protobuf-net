#define VERBOSE // turns on pointer address outputs
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

// see: https://github.com/dotnet/roslyn/issues/24014

static class PossibleCompilerBug
{
    ref struct MutableRefStruct
    {
        public MutableRefStruct(int foo) { _foo = foo; }
        public int Foo => _foo;
        private int _foo;
        public void Incr() => _foo++;
    }
    [Conditional("VERBOSE")]
    unsafe static void ShowAddress(ref this MutableRefStruct val, string name,
        [CallerMemberName] string caller = null)
    {
        fixed(void* ptr = &val)
        {
            var addr = new IntPtr(ptr).ToInt64();
            Console.WriteLine($"{caller}: {name}\t0x{Convert.ToString(addr,16).PadLeft(8,'0')}");
        }
    }
    static void Example1(ref this MutableRefStruct val) // works fine
    {
        ShowAddress(ref val,nameof(val));
        var localCopy = val; // snapshot for rollback (there are reasons)
        ShowAddress(ref localCopy, nameof(localCopy));
        localCopy.Incr();
        val = localCopy; // ldarg0, ldloc0, stobj
    }
    static void Example2(ref this MutableRefStruct val, out int arg) // called incorrectly
    {
        ShowAddress(ref val, nameof(val));
        var localCopy = val; // snapshot for rollback (there are reasons)
        ShowAddress(ref localCopy, nameof(localCopy));
        localCopy.Incr();
        arg = localCopy.Foo;
        val = localCopy; // ldarg0, ldloc0, stobj
    }
    static void Main3()
    {
        var obj = new MutableRefStruct(42);
        Console.WriteLine(obj.Foo); // expect 42, get 42

        // ldloca.s struct2
        // call void PossibleCompilerBug::Example1(valuetype PossibleCompilerBug / MutableRefStruct &)
        obj.Example1();
        Console.WriteLine(obj.Foo); // expect 43, get 43
        
        // ldloc.0 <=============== problem here; this should be ldloca[.s]
        // ldloca.s num
        // call void PossibleCompilerBug::Example2(valuetype PossibleCompilerBug / MutableRefStruct &, int32 &)
        obj.Example2(out _); // boom!
        Console.WriteLine(obj.Foo); // expect 44
    }
}