﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ICSharpCode.Decompiler.ILAst;
using JSIL.Ast;
using JSIL.Internal;
using Mono.Cecil;

namespace JSIL.Transforms {
    public class ExpandCastExpressions : JSAstVisitor {
        public readonly TypeSystem TypeSystem;
        public readonly JSSpecialIdentifiers JS;
        public readonly JSILIdentifier JSIL;
        public readonly ITypeInfoSource TypeInfo;
        public readonly MethodTypeFactory MethodTypeFactory;

        public ExpandCastExpressions (TypeSystem typeSystem, JSSpecialIdentifiers js, JSILIdentifier jsil, ITypeInfoSource typeInfo, MethodTypeFactory methodTypeFactory) {
            TypeSystem = typeSystem;
            JS = js;
            JSIL = jsil;
            TypeInfo = typeInfo;
            MethodTypeFactory = methodTypeFactory;
        }

        public void VisitNode (JSCastExpression ce) {
            var currentType = ce.Expression.GetActualType(TypeSystem);
            var targetType = ce.NewType;

            JSExpression newExpression = null;

            if (targetType.FullName == "System.ValueType") {
                var replacement = ce.Expression;
                ParentNode.ReplaceChild(ce, replacement);
                VisitReplacement(replacement);
                return;
            } else if (
                TypeUtil.IsIntegralOrEnum(currentType) &&
                (targetType.MetadataType == MetadataType.Char)
            ) {
                newExpression = JSInvocationExpression.InvokeStatic(
                    JS.fromCharCode, new[] { ce.Expression }, true
                );
            } else if (
                (currentType.MetadataType == MetadataType.Char) &&
                TypeUtil.IsIntegral(targetType)
            ) {
                newExpression = JSInvocationExpression.InvokeMethod(
                    JS.charCodeAt, ce.Expression, new[] { JSLiteral.New(0) }, true
                );
            } else if (
                IntroduceEnumCasts.IsEnumOrNullableEnum(currentType)
            ) {
                TypeInfo enumInfo;
                var isNullable = TypeUtil.IsNullable(currentType);

                if (isNullable) {
                    int temp;
                    var git = (GenericInstanceType)TypeUtil.FullyDereferenceType(currentType, out temp);
                    enumInfo = TypeInfo.Get(git.GenericArguments[0]);
                } else {
                    enumInfo = TypeInfo.Get(currentType);
                }

                if (enumInfo == null)
                    throw new InvalidOperationException("Unable to extract enum type from typereference " + currentType);

                if (targetType.MetadataType == MetadataType.Boolean) {
                    newExpression = new JSBinaryOperatorExpression(
                        JSOperator.NotEqual,
                        JSCastExpression.New(ce.Expression, TypeSystem.Int32, TypeSystem, true, true),
                        new JSIntegerLiteral(0, typeof(Int32)),
                        TypeSystem.Boolean
                    );
                } else if (TypeUtil.IsNumeric(targetType)) {
                    if (isNullable) {
                        newExpression = JSIL.ValueOfNullable(
                            ce.Expression
                        );
                    } else if (
                        ce.Expression is JSCastExpression &&
                        (((JSCastExpression)ce.Expression).Expression.GetActualType(TypeSystem).MetadataType == MetadataType.Int64 ||
                        ((JSCastExpression)ce.Expression).Expression.GetActualType(TypeSystem).MetadataType == MetadataType.UInt64)
                    ) {
                        newExpression = ce.Expression;
                    } else {
                        newExpression = JSInvocationExpression.InvokeMethod(
                            JS.valueOf(targetType), ce.Expression, null, true
                        );
                    }
                } else if (targetType.FullName == "System.Enum") {
                    newExpression = ce.Expression;
                } else {
                    // Debugger.Break();
                }
            } else if (
                (targetType.MetadataType == MetadataType.Boolean) &&
                (ce.Expression is JSAsExpression) && 
                ((JSAsExpression)ce.Expression).GetActualType(TypeSystem) is GenericParameter
            ) {
                // C# expressions such as (t is T) (where T is a generic parameter). See issue #150. 
                // Tested with AsWithGenericParameter.cs
                newExpression = new JSBinaryOperatorExpression(
                    JSBinaryOperator.NotEqual,
                    ce.Expression, new JSNullLiteral(currentType),
                    TypeSystem.Boolean
                );  
            }
            else if (
                (targetType.MetadataType == MetadataType.Boolean) &&
                // A cast from Object to Boolean can occur in two forms:
                // An implied conversion, where an object expression is treated as a boolean (logicnot operation, etc).
                //  In this case, we want to do 'obj != null' to make it a boolean.
                // An explicit conversion, where an object expression is unboxed to boolean.
                //  In this case we want to leave it as-is.
                (ce.IsCoercion || (currentType.FullName != "System.Object"))
            ) {
                newExpression = new JSBinaryOperatorExpression(
                    JSBinaryOperator.NotEqual,
                    ce.Expression, new JSDefaultValueLiteral(currentType),
                    TypeSystem.Boolean
                );
            } else if (
                TypeUtil.IsNumeric(targetType) &&
                TypeUtil.IsNumeric(currentType) &&
                !TypeUtil.TypesAreEqual(targetType, currentType, true)
            ) {
                if (currentType.MetadataType == MetadataType.Int64) {
                    if (targetType.MetadataType == MetadataType.UInt64) {
                        newExpression = JSInvocationExpression
                            .InvokeMethod(
                                TypeSystem.Int64,
                                new JSFakeMethod("ToUInt64", TypeSystem.Int32, new TypeReference[] { }, MethodTypeFactory),
                                ce.Expression);
                    }
                    else {
                        newExpression = JSInvocationExpression
                            .InvokeMethod(
                                TypeSystem.Int64,
                                new JSFakeMethod("ToNumber", TypeSystem.Int32, new TypeReference[] { }, MethodTypeFactory),
                                ce.Expression);
                    }
                }
                else if (currentType.MetadataType == MetadataType.UInt64) { 
                    if (targetType.MetadataType == MetadataType.Int64) { 
                        newExpression = JSInvocationExpression
                            .InvokeMethod(
                                TypeSystem.Int64,
                                new JSFakeMethod("ToInt64", TypeSystem.Int32, new TypeReference[] { }, MethodTypeFactory),
                                ce.Expression);
                    }
                    else {
                        newExpression = JSInvocationExpression
                            .InvokeMethod(
                                TypeSystem.Int64,
                                new JSFakeMethod("ToNumber", TypeSystem.Int32, new TypeReference[] { }, MethodTypeFactory),
                                ce.Expression);
                    }
                }
                else if (targetType.MetadataType == MetadataType.Int64) {
                    newExpression = JSInvocationExpression.InvokeStatic(
                        new JSType(TypeSystem.Int64),
                        new JSFakeMethod("FromNumber", TypeSystem.Int64, new[] { currentType }, MethodTypeFactory),
                        new[] { ce.Expression },
                        true);
                }
                else if (targetType.MetadataType == MetadataType.UInt64) {
                    newExpression = JSInvocationExpression.InvokeStatic(
                        new JSType(TypeSystem.UInt64),
                        new JSFakeMethod("FromNumber", TypeSystem.UInt64, new[] { currentType }, MethodTypeFactory),
                        new[] { ce.Expression },
                        true);
                }
                else if (
                    TypeUtil.IsIntegral(currentType) ||
                    !TypeUtil.IsIntegral(targetType))
                {
                    // Ensure that we don't eliminate casts that reduce the size of a value
                    if (TypeUtil.SizeOfType(currentType) < TypeUtil.SizeOfType(targetType))
                        newExpression = ce.Expression;
                }
                else
                {
                    newExpression = new JSTruncateExpression(ce.Expression);
                }
            } else {
                // newExpression = JSIL.Cast(ce.Expression, targetType);
            }

            if (newExpression != null) {
                ParentNode.ReplaceChild(ce, newExpression);
                VisitReplacement(newExpression);
            } else {
                // Debugger.Break();
                VisitChildren(ce);
            }
        }
    }
}
