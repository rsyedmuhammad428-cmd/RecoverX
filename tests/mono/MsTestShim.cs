// Verification harness only (NOT shipped in the app): a minimal stand-in for MSTest so the managed tests can run under Mono on Linux.
// The real tests in tests/CoreTests use Microsoft.VisualStudio.TestTools.UnitTesting and run with `dotnet test` on Windows.
using System;
using System.Linq;
using System.Reflection;
namespace Microsoft.VisualStudio.TestTools.UnitTesting {
    [AttributeUsage(AttributeTargets.Class)] public class TestClassAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public class TestMethodAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public class TestInitializeAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public class TestCleanupAttribute : Attribute { }
    public class AssertFailedException : Exception { public AssertFailedException(string m) : base(m) { } }
    public class AssertInconclusiveException : Exception { public AssertInconclusiveException(string m) : base(m) { } }
    public static class Assert {
        public static void AreEqual(object e, object a) { if (!object.Equals(e, a)) throw new AssertFailedException("expected <" + e + "> actual <" + a + ">"); }
        public static void AreEqual(int e, int a) { if (e != a) throw new AssertFailedException("expected <" + e + "> actual <" + a + ">"); }
        public static void AreEqual(long e, long a) { if (e != a) throw new AssertFailedException("expected <" + e + "> actual <" + a + ">"); }
        public static void IsTrue(bool c) { if (!c) throw new AssertFailedException("IsTrue failed"); }
        public static void IsTrue(bool c, string m) { if (!c) throw new AssertFailedException("IsTrue failed: " + m); }
        public static void IsFalse(bool c, string m) { if (c) throw new AssertFailedException("IsFalse failed: " + m); }
        public static void AreEqual(string e, string a) { if (e != a) throw new AssertFailedException("expected <" + e + "> actual <" + a + ">"); }
        public static void IsNotNull(object o) { if (o == null) throw new AssertFailedException("IsNotNull failed"); }
        public static void IsFalse(bool c) { if (c) throw new AssertFailedException("IsFalse failed"); }
        public static void IsNull(object o) { if (o != null) throw new AssertFailedException("IsNull failed: " + o); }
        public static void Fail(string m) { throw new AssertFailedException(m); }
        public static void Inconclusive(string m) { throw new AssertInconclusiveException(m); }
    }
    public static class StringAssert { public static void Contains(string v, string s) { if (v == null || !v.Contains(s)) throw new AssertFailedException("'" + v + "' does not contain '" + s + "'"); } }
    public static class CollectionAssert {
        public static void AreEqual(System.Collections.ICollection e, System.Collections.ICollection a) {
            if (e is byte[] && a is byte[]) { if (!((byte[])e).SequenceEqual((byte[])a)) throw new AssertFailedException("byte arrays differ"); return; }
            var x = e.Cast<object>().ToArray(); var y = a.Cast<object>().ToArray();
            if (x.Length != y.Length || !x.Zip(y, (p, q) => object.Equals(p, q)).All(b => b)) throw new AssertFailedException("collections differ: [" + string.Join(",", x) + "] vs [" + string.Join(",", y) + "]");
        }
    }
    public static class Runner {
        public static int Run(Assembly asm) { string filter = Environment.GetEnvironmentVariable("TEST_FILTER") ?? "";
            int pass = 0, fail = 0, inc = 0;
            foreach (var t in asm.GetTypes().Where(t => t.GetCustomAttributes(typeof(TestClassAttribute), false).Any())) {
                foreach (var m in t.GetMethods().Where(m => m.GetCustomAttributes(typeof(TestMethodAttribute), false).Any() && (t.Name + "." + m.Name).Contains(filter))) {
                    var obj = Activator.CreateInstance(t);
                    Action<Type> call = a => { foreach (var x in t.GetMethods().Where(z => z.GetCustomAttributes(a, false).Any())) x.Invoke(obj, null); };
                    try { call(typeof(TestInitializeAttribute)); m.Invoke(obj, null); pass++; Console.WriteLine("PASS  " + t.Name + "." + m.Name); }
                    catch (TargetInvocationException ex) when (ex.InnerException is AssertInconclusiveException) { inc++; Console.WriteLine("SKIP  " + t.Name + "." + m.Name + ": " + ex.InnerException.Message); }
                    catch (TargetInvocationException ex) { fail++; Console.WriteLine("FAIL  " + t.Name + "." + m.Name + ": " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message); }
                    finally { try { call(typeof(TestCleanupAttribute)); } catch { } }
                }
            }
            Console.WriteLine("\n{0} passed, {1} failed, {2} inconclusive", pass, fail, inc);
            return fail == 0 ? 0 : 1;
        }
    }
    public static class Program { public static int Main() { return Runner.Run(Assembly.GetEntryAssembly()); } }
}
