﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace JSIL.Tests {
    [TestFixture]
    public class ComparisonTests : GenericTestFixture {
        [Test]
        public void HelloWorld () {
            using (var test = new ComparisonTest(@"TestCases\HelloWorld.cs")) {
                test.Run();
                test.Run("hello", "world");
            }
        }

        [Test]
        public void BinaryTrees () {
            using (var test = new ComparisonTest(@"TestCases\BinaryTrees.cs")) {
                test.Run();
                test.Run("8");
            }
        }

        [Test]
        public void NBody () {
            using (var test = new ComparisonTest(@"TestCases\NBody.cs")) {
                test.Run();
                test.Run("50000");
            }
        }

        [Test]
        public void FannkuchRedux () {
            using (var test = new ComparisonTest(@"TestCases\FannkuchRedux.cs")) {
                test.Run();
                test.Run("10");
            }
        }

        [Test]
        public void AllSimpleTests () {
            var simpleTests = Directory.GetFiles(
                Path.GetFullPath(Path.Combine(ComparisonTest.TestSourceFolder, "SimpleTestCases")), 
                "*.cs"
            );
            var failureList = new List<string>();

            foreach (var filename in simpleTests) {
                Console.Write("// {0} ... ", Path.GetFileName(filename));

                try {
                    using (var test = new ComparisonTest(filename))
                        test.Run();
                } catch (Exception ex) {
                    Console.Error.WriteLine(ex);
                    failureList.Add(Path.GetFileNameWithoutExtension(filename));
                }
            }

            Assert.AreEqual(0, failureList.Count, 
                String.Format("{0} test(s) failed:\r\n{1}", failureList.Count, String.Join("\r\n", failureList.ToArray()))
            );
        }

        [Test]
        public void LambdaTests () {
            using (var test = new ComparisonTest(@"TestCases\Lambdas.cs"))
                test.Run();

            using (var test = new ComparisonTest(@"TestCases\LambdasUsingLocals.cs"))
                test.Run();
        }

        [Test]
        public void Goto () {
            using (var test = new ComparisonTest(@"TestCases\Goto.cs"))
                test.Run();
        }

        [Test]
        public void YieldReturn () {
            using (var test = new ComparisonTest(@"TestCases\YieldReturn.cs"))
                test.Run();
        }

        [Test]
        public void Switch () {
            using (var test = new ComparisonTest(@"TestCases\Switch.cs"))
                test.Run();
        }

        [Test]
        public void IntegerArithmetic () {
            using (var test = new ComparisonTest(@"TestCases\IntegerArithmetic.cs"))
                test.Run();
        }

        [Test]
        public void InterleavedTemporaries () {
            using (var test = new ComparisonTest(@"TestCases\InterleavedTemporaries.cs"))
                test.Run();
        }

        [Test]
        public void IndirectInterleavedTemporaries () {
            using (var test = new ComparisonTest(@"TestCases\IndirectInterleavedTemporaries.cs"))
                test.Run();
        }
    }
}
