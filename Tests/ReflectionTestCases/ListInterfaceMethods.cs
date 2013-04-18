﻿using System;
using System.Reflection;

public interface A {
    void MethodA ();
    void MethodA (int i);
    int MethodB ();
}

public interface B : A {
    float MethodC ();
}

public static class Program {
    public static void Main (string[] args) {
        Console.WriteLine("A");
        Common.Util.ListMembers<MethodInfo>(
            typeof(A),
            BindingFlags.Instance
        );

        Common.Util.ListMembers<MethodInfo>(
            typeof(A),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
        );

        Console.WriteLine("B");
        Common.Util.ListMembers<MethodInfo>(
            typeof(B),
            BindingFlags.Instance
        );

        Common.Util.ListMembers<MethodInfo>(
            typeof(B),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
        );
    }
}