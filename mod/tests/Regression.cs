using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

// Compile with csc, then run from a test copy of Managed containing the installed mod.
class Regression
{
    static void Check(bool ok, string test)
    {
        if (!ok) throw new Exception(test);
        Console.WriteLine("PASS " + test);
    }

    static void Main()
    {
        var mod = Assembly.LoadFrom("ClusterTruckCoop.dll");
        var plugin = mod.GetType("ClusterTruckCoop.CoopPlugin", true);
        var validate = (Func<byte[], int, bool>)Delegate.CreateDelegate(
            typeof(Func<byte[], int, bool>), plugin.GetMethod("CompleteClientState", BindingFlags.Static | BindingFlags.NonPublic));
        byte[] packet = new byte[128];
        packet[0] = (byte)'P';
        // The existing wire format has 58 fixed bytes plus the UTF-8 name.
        for (int name = 0; name <= 60; name++)
        {
            packet[43] = (byte)name;
            int length = 58 + name;
            for (int end = 0; end < length; end++)
                if (validate(packet, end)) throw new Exception("accepted incomplete frame");
            if (!validate(packet, length) || validate(packet, length + 1)) throw new Exception("frame size");
        }
        Check(true, "wire lengths and truncation");
        packet[43] = 61;
        Check(!validate(packet, 119), "name limit");
        packet[43] = 0;
        packet[57] = 2;
        Check(!validate(packet, 58), "pause flag");
        packet[57] = 0;
        int accepted = 0;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 5000000; i++) if (validate(packet, 58)) accepted++;
        watch.Stop();
        Check(accepted == 5000000, "valid frames");
        Console.WriteLine("5M validations: " + watch.Elapsed.TotalMilliseconds + " ms");

        var race = mod.GetType("ClusterTruckCoop.Race", true);
        var entry = race.GetNestedType("Entry");
        var entries = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entry));
        for (int i = 0; i < 2; i++)
        {
            var e = Activator.CreateInstance(entry);
            entry.GetField("player").SetValue(e, new object());
            entry.GetField("slot").SetValue(e, i);
            entry.GetField("name").SetValue(e, "QA");
            entry.GetField("points").SetValue(e, (float)(10 + i));
            entries.Add(e);
        }
        var record = race.GetMethod("Record", BindingFlags.NonPublic | BindingFlags.Static);
        object[] args = { 1, 1, entries };
        Check((bool)record.Invoke(null, args), "first result");
        Check(!(bool)record.Invoke(null, args), "duplicate frame");
        var totals = (IDictionary)race.GetField("totals", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        Check(totals.Count == 2, "duplicate names kept separate");
        foreach (object e in entries)
        {
            var total = totals[entry.GetField("player").GetValue(e)];
            Check(entry.GetField("points").GetValue(e).Equals(total.GetType().GetField("points").GetValue(total)), "individual points");
        }
        race.GetMethod("Reset").Invoke(null, null);
        Check(totals.Count == 0 && (bool)record.Invoke(null, args), "new room reuses epoch with fresh totals");
    }
}
