﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ICSharpCode.Decompiler.ILAst;
using JSIL.Internal;
using JSIL.Transforms;
using Mono.Cecil;

namespace JSIL.Ast {
    public abstract class JSNode {
        /// <summary>
        /// Enumerates the children of this node.
        /// </summary>
        public virtual IEnumerable<JSNode> Children {
            get {
                yield break;
            }
        }

        public IEnumerable<JSNode> AllChildrenRecursive {
            get {
                foreach (var child in Children) {
                    if (child == null)
                        continue;

                    yield return child;

                    foreach (var subchild in child.AllChildrenRecursive) {
                        if (subchild == null)
                            continue;

                        yield return subchild;
                    }
                }
            }
        }

        public virtual bool IsNull {
            get {
                return false;
            }
        }

        public abstract void ReplaceChild (JSNode oldChild, JSNode newChild);
    }

    public abstract class JSStatement : JSNode {
        public static readonly JSNullStatement Null = new JSNullStatement();

        public string Label = null;

        public override void ReplaceChild (JSNode oldChild, JSNode newChild) {
            if (oldChild == null)
                throw new ArgumentNullException();

            throw new NotImplementedException(
                String.Format("Statements of type '{0}' do not support child replacement", GetType().Name)
            );
        }
    }

    public class JSNullStatement : JSStatement {
        public override bool IsNull {
            get {
                return true;
            }
        }

        public override string ToString () {
            return "<Null>";
        }
    }

    public class JSGotoExpression : JSNullExpression {
        public readonly string TargetLabel;

        public JSGotoExpression (string targetLabel) {
            TargetLabel = targetLabel;
        }

        public override bool Equals (object obj) {
            var rhs = obj as JSGotoExpression;

            if (rhs == null)
                return base.Equals(obj);

            return String.Equals(TargetLabel, rhs.TargetLabel);
        }

        public override string ToString () {
            return String.Format("goto {0}", TargetLabel);
        }
    }

    public class JSBlockStatement : JSStatement {
        public readonly List<JSStatement> Statements;

        public JSBlockStatement (params JSStatement[] statements) {
            Statements = new List<JSStatement>(statements);
        }

        public override IEnumerable<JSNode> Children {
            get {
                for (int i = 0, c = Statements.Count; i < c; i++)
                    yield return Statements[i];
            }
        }

        public override void ReplaceChild (JSNode oldChild, JSNode newChild) {
            if (oldChild == null)
                throw new ArgumentNullException();

            var stmt = (JSStatement)newChild;

            for (int i = 0, c = Statements.Count; i < c; i++) {
                if (Statements[i] == oldChild)
                    Statements[i] = stmt;
            }
        }

        public override string ToString () {
            var sb = new StringBuilder();

            foreach (var stmt in Statements)
                sb.AppendLine(String.Concat(stmt));

            return sb.ToString();
        }
    }

    public class JSLabelGroupStatement : JSBlockStatement {
        public readonly int GroupIndex;

        public JSLabelGroupStatement (int index, params JSBlockStatement[] labelledBlocks) {
            GroupIndex = index;

            Statements.AddRange(labelledBlocks);
        }
    }

    public class JSVariableDeclarationStatement : JSStatement {
        public readonly List<JSBinaryOperatorExpression> Declarations = new List<JSBinaryOperatorExpression>();

        public JSVariableDeclarationStatement (params JSBinaryOperatorExpression[] declarations) {
            Declarations.AddRange(declarations);
        }

        public override IEnumerable<JSNode> Children {
            get {
                return Declarations;
            }
        }

        public override void ReplaceChild (JSNode oldChild, JSNode newChild) {
            if (oldChild == null)
                throw new ArgumentNullException();

            var boe = newChild as JSBinaryOperatorExpression;

            if (boe == null)
                Declarations.RemoveAll((c) => c == oldChild);
            else
                for (int i = 0, c = Declarations.Count; i < c; i++) {
                    if (Declarations[i] == oldChild)
                        Declarations[i] = boe;
                }
        }

        public override string ToString () {
            return String.Format(
                "var {0}",
                String.Join(", ", (from d in Declarations select String.Concat(d)).ToArray())
            );
        }
    }

    public class JSLabelStatement : JSStatement {
        public JSLabelStatement (string name) {
            Label = name;
        }
    }

    public class JSExpressionStatement : JSStatement {
        protected JSExpression _Expression;

        public JSExpressionStatement (JSExpression expression) {
            _Expression = expression;
        }

        public override IEnumerable<JSNode> Children {
            get {
                yield return _Expression;
            }
        }

        public JSExpression Expression {
            get {
                return _Expression;
            }
        }

        public override void ReplaceChild (JSNode oldChild, JSNode newChild) {
            if (oldChild == null)
                throw new ArgumentNullException();

            if (oldChild == _Expression)
                _Expression = (JSExpression)newChild;
        }

        public override string ToString () {
            return String.Format("~ {0}", _Expression);
        }
    }

    public class JSFunctionExpression : JSExpression {
        public readonly MethodDefinition OriginalMethod;
        public readonly MethodReference OriginalMethodReference;

        public readonly Dictionary<string, JSVariable> AllVariables;
        // This has to be JSVariable, because 'this' is of type (JSVariableReference<JSThisParameter>) for structs
        // We also need to make this an IEnumerable, so it can be a select expression instead of a constant array
        public readonly IEnumerable<JSVariable> Parameters;
        public readonly JSBlockStatement Body;

        public JSFunctionExpression (
            MethodDefinition originalMethod, MethodReference originalMethodReference, Dictionary<string, JSVariable> allVariables, IEnumerable<JSVariable> parameters, JSBlockStatement body
        ) {
            OriginalMethod = originalMethod;
            OriginalMethodReference = originalMethodReference;
            AllVariables = allVariables;
            Parameters = parameters;
            Body = body;
        }

        public override IEnumerable<JSNode> Children {
            get {
                foreach (var parameter in Parameters)
                    yield return parameter;

                yield return Body;
            }
        }

        public override bool Equals (object obj) {
            var rhs = obj as JSFunctionExpression;
            if (rhs != null) {
                if (!Object.Equals(OriginalMethod, rhs.OriginalMethod))
                    return false;
                if (!Object.Equals(AllVariables, rhs.AllVariables))
                    return false;
                if (!Object.Equals(Parameters, rhs.Parameters))
                    return false;

                return EqualsImpl(obj, true);
            }

            return EqualsImpl(obj, true);
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            if (OriginalMethod != null) {
                var delegateType = ConstructDelegateType(OriginalMethodReference, typeSystem);
                if (delegateType == null)
                    return OriginalMethod.ReturnType;
                else
                    return delegateType;
            } else
                return typeSystem.Void;
        }
    }

    // Technically, the following expressions should be statements. But in ILAst, they're expressions...
    public class JSReturnExpression : JSExpression {
        public JSReturnExpression (JSExpression value = null)
            : base (value) {
        }

        public JSExpression Value {
            get {
                return Values[0];
            }
        }
    }

    public class JSThrowExpression : JSExpression {
        public JSThrowExpression (JSExpression exception = null)
            : base (exception) {
        }

        public JSExpression Exception {
            get {
                return Values[0];
            }
        }
    }

    public class JSBreakExpression : JSExpression {
        public string TargetLabel;

    }

    public class JSContinueExpression : JSExpression {
        public string TargetLabel;

    }

    public class JSSwitchCase : JSStatement {
        public readonly JSExpression[] Values;
        public readonly JSBlockStatement Body;

        public JSSwitchCase (JSExpression[] values, JSBlockStatement body) {
            Values = values;
            Body = body;
        }

        public override IEnumerable<JSNode> Children {
            get {
                if (Values != null) {
                    foreach (var value in Values)
                        yield return value;
                }

                yield return Body;
            }
        }
    }

    public class JSSwitchStatement : JSStatement {
        protected JSExpression _Condition;
        public readonly List<JSSwitchCase> Cases = new List<JSSwitchCase>();

        public JSSwitchStatement (JSExpression condition, params JSSwitchCase[] cases) {
            _Condition = condition;
            Cases.AddRange(cases);
        }

        public override IEnumerable<JSNode> Children {
            get {
                yield return _Condition;

                foreach (var c in Cases)
                    yield return c;
            }
        }

        public JSExpression Condition {
            get {
                return _Condition;
            }
        }

        public override void ReplaceChild (JSNode oldChild, JSNode newChild) {
            if (oldChild == null)
                throw new ArgumentNullException();

            if (_Condition == oldChild)
                _Condition = (JSExpression)newChild;

            var cse = newChild as JSSwitchCase;

            if (cse != null) {
                for (int i = 0, c = Cases.Count; i < c; i++) {
                    if (Cases[i] == oldChild)
                        Cases[i] = cse;
                }
            }
        }
    }

    public class JSIfStatement : JSStatement {
        protected JSExpression _Condition;
        protected JSStatement _TrueClause, _FalseClause;

        public JSIfStatement (JSExpression condition, JSStatement trueClause, JSStatement falseClause = null) {
            _Condition = condition;
            _TrueClause = trueClause;
            _FalseClause = falseClause;
        }

        public static JSIfStatement New (params KeyValuePair<JSExpression, JSStatement>[] conditions) {
            if ((conditions == null) || (conditions.Length == 0))
                throw new ArgumentException("conditions");

            JSIfStatement result = new JSIfStatement(
                conditions[0].Key, conditions[0].Value
            );
            JSIfStatement next = null, current = result;

            for (int i = 1; i < conditions.Length; i++) {
                var cond = conditions[i].Key;

                if (cond != null) {
                    next = new JSIfStatement(cond, conditions[i].Value);
                    current._FalseClause = next;
                    current = next;
                } else
                    current._FalseClause = conditions[i].Value;
            }

            return result;
        }

        public override IEnumerable<JSNode> Children {
            get {
                yield return _Condition;
                yield return _TrueClause;

                if (_FalseClause != null)
                    yield return _FalseClause;
            }
        }

        public JSExpression Condition {
            get {
                return _Condition;
            }
        }

        public JSStatement TrueClause {
            get {
                return _TrueClause;
            }
        }

        public JSStatement FalseClause {
            get {
                return _FalseClause;
            }
        }

        public override void ReplaceChild (JSNode oldChild, JSNode newChild) {
            if (oldChild == null)
                throw new ArgumentNullException();

            if (_Condition == oldChild)
                _Condition = (JSExpression)newChild;

            if (_TrueClause == oldChild)
                _TrueClause = (JSStatement)newChild;

            if (_FalseClause == oldChild)
                _FalseClause = (JSStatement)newChild;
        }

        public override string ToString () {
            return String.Format(
                "if ({0}) {{\r\n{1}\r\n}} else {{\r\n{2}\r\n}}", 
                _Condition, Util.Indent(_TrueClause), Util.Indent(_FalseClause)
            );
        }
    }

    public class JSWhileLoop : JSBlockStatement {
        protected JSExpression _Condition;

        public JSWhileLoop (JSExpression condition, params JSStatement[] body) {
            _Condition = condition;
            Statements.AddRange(body);
        }

        public override IEnumerable<JSNode> Children {
            get {
                yield return _Condition;

                foreach (var s in base.Children)
                    yield return s;
            }
        }

        public JSExpression Condition {
            get {
                return _Condition;
            }
        }

        public override void ReplaceChild (JSNode oldChild, JSNode newChild) {
            if (oldChild == null)
                throw new ArgumentNullException();

            if (_Condition == oldChild)
                _Condition = (JSExpression)newChild;

            if (newChild is JSStatement)
                base.ReplaceChild(oldChild, newChild);
        }

        public override string ToString () {
            return String.Format(
                "while ({0}) {{\r\n{1}\r\n}}",
                _Condition, Util.Indent(base.ToString())
            );
        }
    }

    public class JSTryCatchBlock : JSStatement {
        public readonly JSStatement Body;
        public JSVariable CatchVariable;
        public JSStatement Catch;
        public JSStatement Finally;

        public JSTryCatchBlock (JSStatement body, JSVariable catchVariable = null, JSStatement @catch = null, JSStatement @finally = null) {
            Body = body;
            CatchVariable = catchVariable;
            Catch = @catch;
            Finally = @finally;
        }

        public override IEnumerable<JSNode> Children {
            get {
                yield return Body;

                if (CatchVariable != null)
                    yield return CatchVariable;
                if (Catch != null)
                    yield return Catch;
                if (Finally != null)
                    yield return Finally;
            }
        }
    }

    public abstract class JSExpression : JSNode {
        protected struct MethodSignature {
            public readonly TypeReference ReturnType;
            public readonly IEnumerable<TypeReference> ParameterTypes;
            public readonly int ParameterCount;
            private readonly int HashCode;

            public MethodSignature (TypeReference returnType, IEnumerable<TypeReference> parameterTypes) {
                ReturnType = returnType;
                ParameterTypes = parameterTypes;
                ParameterCount = parameterTypes.Count();

                HashCode = ReturnType.FullName.GetHashCode() ^ ParameterCount;

                int i = 0;
                foreach (var p in ParameterTypes) {
                    HashCode ^= (p.FullName.GetHashCode() << i);
                    i += 1;
                }
            }

            public override int GetHashCode () {
                return HashCode;
            }

            public bool Equals (MethodSignature rhs) {
                if (!ILBlockTranslator.TypesAreEqual(
                    ReturnType, rhs.ReturnType
                ))
                    return false;

                if (ParameterCount != rhs.ParameterCount)
                    return false;

                using (var e1 = ParameterTypes.GetEnumerator())
                using (var e2 = rhs.ParameterTypes.GetEnumerator())
                while (e1.MoveNext() && e2.MoveNext()) {
                    if (!ILBlockTranslator.TypesAreEqual(e1.Current, e2.Current))
                        return false;
                }

                return true;
            }

            public override bool Equals (object obj) {
                if (obj is MethodSignature)
                    return Equals((MethodSignature)obj);
                else
                    return base.Equals(obj);
            }
        }

        public static readonly JSNullExpression Null = new JSNullExpression();
        protected static readonly Dictionary<MethodSignature, TypeReference> MethodTypeCache = new Dictionary<MethodSignature, TypeReference>();

        protected readonly IList<JSExpression> Values;

        protected JSExpression (params JSExpression[] values) {
            Values = values;
        }

        public override IEnumerable<JSNode> Children {
            get {
                // We don't want to use foreach here, since a value could be changed during iteration
                for (int i = 0, c = Values.Count; i < c; i++)
                    yield return Values[i];
            }
        }

        public override string ToString () {
            return String.Format(
                "{0}[{1}]", GetType().Name,
                String.Join(", ", (from v in Values select String.Concat(v)).ToArray())
            );
        }

        public virtual TypeReference GetExpectedType (TypeSystem typeSystem) {
            throw new NoExpectedTypeException(this);
        }

        public static TypeReference DeReferenceType (TypeReference type, bool once = false) {
            var brt = type as ByReferenceType;

            while (brt != null) {
                type = brt.ElementType;
                brt = type as ByReferenceType;

                if (once)
                    break;
            }

            return type;
        }

        public static bool IsOpenGenericType (ParameterDefinition pd) {
            return IsOpenGenericType(pd.ParameterType);
        }

        public static bool IsOpenGenericType (TypeReference type) {
            type = DeReferenceType(type);

            if (type.IsGenericParameter)
                return true;

            foreach (var gp in type.GenericParameters)
                if (IsOpenGenericType(gp))
                    return true;

            var git = type as GenericInstanceType;
            if (git != null) {
                foreach (var ga in git.GenericArguments)
                    if (IsOpenGenericType(ga))
                        return true;
            }

            var modopt = type as OptionalModifierType;
            if (modopt != null)
                return IsOpenGenericType(modopt.ElementType);

            var modreq = type as RequiredModifierType;
            if (modreq != null)
                return IsOpenGenericType(modreq.ElementType);

            return false;
        }

        public static MethodReference ResolveGenericMethod (MethodReference method) {
            if (!IsOpenGenericType(method.ReturnType) &&
                !method.Parameters.Any(IsOpenGenericType))
                return method;

            TypeReference returnType;
            returnType = TypeAnalysis.SubstituteTypeArgs(method.ReturnType, method);

            var result = new MethodReference(
                method.Name, returnType, method.DeclaringType
            );

            foreach (var parameter in method.Parameters)
                result.Parameters.Add(new ParameterDefinition(
                    parameter.Name, parameter.Attributes,
                    TypeAnalysis.SubstituteTypeArgs(parameter.ParameterType, method)
                ));

            result.HasThis = method.HasThis;
            result.ExplicitThis = method.ExplicitThis;
            result.CallingConvention = method.CallingConvention;
            result.DeclaringType = method.DeclaringType;

            if (IsOpenGenericType(result.ReturnType) ||
                result.Parameters.Any(IsOpenGenericType))
                throw new AbortTranslation(String.Format("Failed to resolve generic method '{0}'.", method));

            return result;
        }

        public static TypeReference ConstructDelegateType (MethodReference method, TypeSystem typeSystem) {
            method = ResolveGenericMethod(method);

            try {
                return ConstructDelegateType(
                    TypeAnalysis.SubstituteTypeArgs(method.ReturnType, method),
                    (from p in method.Parameters
                     select TypeAnalysis.SubstituteTypeArgs(p.ParameterType, method)),
                     typeSystem
                );
            } catch (OpenGenericTypeException) {
                throw;
            }
        }

        public static TypeReference ConstructDelegateType (TypeReference returnType, IEnumerable<TypeReference> parameterTypes, TypeSystem typeSystem) {
            TypeReference result;
            var signature = new MethodSignature(returnType, parameterTypes);

            if (MethodTypeCache.TryGetValue(signature, out result))
                return result;

            TypeReference genericDelegateType;

            var systemModule = typeSystem.Boolean.Resolve().Module;
            bool hasReturnType;

            if (ILBlockTranslator.TypesAreEqual(typeSystem.Void, returnType)) {
                hasReturnType = false;
                var name = String.Format("System.Action`{0}", signature.ParameterCount);
                genericDelegateType = systemModule.GetType(
                    signature.ParameterCount == 0 ? "System.Action" : name
                );
            } else {
                hasReturnType = true;
                genericDelegateType = systemModule.GetType(String.Format(
                    "System.Func`{0}", signature.ParameterCount + 1
                ));
            }

            if (genericDelegateType != null) {
                var git = new GenericInstanceType(genericDelegateType);
                foreach (var pt in parameterTypes)
                    git.GenericArguments.Add(pt);

                if (hasReturnType)
                    git.GenericArguments.Add(returnType);

                MethodTypeCache[signature] = git;
                return git;
            } else {
                var baseType = systemModule.GetType("System.MulticastDelegate");

                var td = new TypeDefinition(
                    "JSIL.Meta", "MethodSignature", TypeAttributes.Class | TypeAttributes.NotPublic, baseType
                );
                td.DeclaringType = baseType;

                var invoke = new MethodDefinition(
                    "Invoke", MethodAttributes.Public, returnType
                );
                foreach (var pt in parameterTypes)
                    invoke.Parameters.Add(new ParameterDefinition(pt));

                td.Methods.Add(invoke);

                MethodTypeCache[signature] = td;
                return td;
            }
        }

        public override void ReplaceChild (JSNode oldChild, JSNode newChild) {
            if (oldChild == null)
                throw new ArgumentNullException();
            if ((oldChild == this) || (newChild == this))
                throw new InvalidOperationException("Infinite recursion");

            var expr = (JSExpression)newChild;

            for (int i = 0, c = Values.Count; i < c; i++) {
                if (Values[i] == oldChild)
                    Values[i] = expr;
            }
        }

        protected bool EqualsImpl (object obj, bool fieldsChecked) {
            if (this == obj)
                return true;
            else if (obj == null)
                return false;
            else if (obj.GetType() != GetType())
                return false;

            var rhs = (JSExpression)obj;
            if (Values.Count != rhs.Values.Count)
                return false;

            if ((Values.Count == 0) && (!fieldsChecked))
                throw new NotImplementedException(String.Format("Expressions of type {0} cannot be compared", GetType().Name));

            for (int i = 0, c = Values.Count; i < c; i++) {
                var lhsV = Values[i];
                var rhsV = rhs.Values[i];

                if (!lhsV.Equals(rhsV))
                    return false;
            }

            return true;
        }

        public virtual bool IsConstant {
            get {
                return false;
            }
        }

        public override bool Equals (object obj) {
            return EqualsImpl(obj, false);
        }
    }

    // Indicates that the contained expression is a constructed reference to a JS value.
    public class JSReferenceExpression : JSExpression {
        protected JSReferenceExpression (JSExpression referent)
            : base (referent) {
        }

        /// <summary>
        /// Converts a constructed reference into the expression it refers to, turning it back into a regular expression.
        /// </summary>
        public static bool TryDereference (JSILIdentifier jsil, JSExpression reference, out JSExpression referent) {
            var variable = reference as JSVariable;
            var refe = reference as JSReferenceExpression;
            var boe = reference as JSBinaryOperatorExpression;

            var expressionType = reference.GetExpectedType(jsil.TypeSystem);
            if (ILBlockTranslator.IsIgnoredType(expressionType)) {
                referent = new JSUntranslatableExpression(expressionType.FullName);
                return true;
            }

            if ((boe != null) && (boe.Operator is JSAssignmentOperator)) {
                if (TryDereference(jsil, boe.Right, out referent))
                    return true;
            }

            if (variable != null) {
                if (variable.IsReference) {
                    var rv = variable.Dereference();

                    // Since a 'ref' parameter looks just like an implicit reference created by
                    //  a field load, we need to detect cases where we are erroneously stripping
                    //  an argument's reference modifier
                    if (variable.IsParameter) {
                        if (rv.IsReference != variable.GetParameter().IsReference)
                            rv = variable.GetParameter();
                    }

                    referent = rv;
                    return true;
                }
            }
            
            if (refe == null) {
                referent = null;
                return false;
            }

            referent = refe.Referent;
            return true;
        }

        /// <summary>
        /// Converts a constructed reference into an actual reference to the expression it refers to, allowing it to be passed to functions.
        /// </summary>
        public static bool TryMaterialize (JSILIdentifier jsil, JSExpression reference, out JSExpression materialized) {
            var mref = reference as JSMemberReferenceExpression;

            if (mref != null) {
                var referent = mref.Referent;
                while (referent is JSReferenceExpression)
                    referent = ((JSReferenceExpression)referent).Referent;

                var dot = referent as JSDotExpression;

                if (dot != null) {
                    materialized = jsil.NewMemberReference(
                        dot.Target, dot.Member.ToLiteral()
                    );
                    return true;
                }
            }

            var variable = reference as JSVariable;
            var refe = reference as JSReferenceExpression;
            if (refe != null)
                variable = refe.Referent as JSVariable;

            if ((variable != null) && (variable.IsReference)) {
                materialized = variable.Dereference();
                return true;
            }

            materialized = null;
            return false;
        }

        public static JSExpression New (JSExpression referent) {
            var variable = referent as JSVariable;

            if ((variable != null) && (variable.IsReference)) {
                return variable;
            } else {
                return new JSReferenceExpression(referent);
            }
        }

        public JSExpression Referent {
            get {
                return Values[0];
            }
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return new ByReferenceType(Referent.GetExpectedType(typeSystem));
        }

        public override string ToString () {
            return String.Format("&({0})", Referent);
        }
    }

    public class JSMemberReferenceExpression : JSReferenceExpression {
        public JSMemberReferenceExpression (JSExpression referent)
            : base(referent) {
        }
    }

    // Indicates that the contained expression needs to be passed by reference.
    public class JSPassByReferenceExpression : JSExpression {
        public JSPassByReferenceExpression (JSExpression referent)
            : base(referent) {

            if (referent is JSPassByReferenceExpression)
                throw new InvalidOperationException("Nested references are not allowed");
        }

        public JSExpression Referent {
            get {
                return Values[0];
            }
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return Referent.GetExpectedType(typeSystem);
        }

        public override string ToString () {
            return String.Format("<ref {0}>", Referent);
        }
    }

    public class JSNullExpression : JSExpression {
        public override bool IsNull {
            get {
                return true;
            }
        }

        public override string ToString () {
            return "<Null>";
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return typeSystem.Void;
        }
    
        public override bool Equals (object obj) {
            return EqualsImpl(obj, true);
        }
    }

    public class JSUntranslatableStatement : JSNullStatement {
        public readonly object Type;

        public JSUntranslatableStatement (object type) {
            Type = type;
        }

        public override string ToString () {
            return String.Format("Untranslatable Statement {0}", Type);
        }
    }

    public class JSUntranslatableExpression : JSNullExpression {
        public readonly object Type;

        public JSUntranslatableExpression (object type) {
            Type = type;
        }

        public override string ToString () {
            return String.Format("Untranslatable Expression {0}", Type);
        }

        public override bool Equals (object obj) {
            var rhs = obj as JSUntranslatableExpression;
            if (rhs != null) {
                if (!Object.Equals(Type, rhs.Type))
                    return false;
            }

            return EqualsImpl(obj, true);
        }
    }

    public class JSIgnoredMemberReference : JSNullExpression {
        public readonly bool ThrowError;
        public readonly IMemberInfo Member;
        public readonly JSExpression[] Arguments;

        public JSIgnoredMemberReference (bool throwError, IMemberInfo member, params JSExpression[] arguments) {
            /*
            if ((member == null) && ((arguments == null) || (arguments.Length == 0)))
                throw new ArgumentNullException();
             */

            ThrowError = throwError;
            Member = member;
            Arguments = arguments;
        }

        public override string ToString () {
            if (Member != null)
                return String.Format("Reference to ignored member {0}", Member.Name);
            else
                return "Reference to ignored member (no info)";
        }

        public override IEnumerable<JSNode> Children {
            get {
                foreach (var arg in Arguments)
                    yield return arg;
            }
        }

        public override bool Equals (object obj) {
            var rhs = obj as JSIgnoredMemberReference;
            if (rhs != null) {
                if (!Object.Equals(Member, rhs.Member))
                    return false;

                if (Arguments.Length != rhs.Arguments.Length)
                    return false;

                for (int i = 0; i < Arguments.Length; i++)
                    if (!Arguments[i].Equals(rhs.Arguments[i]))
                        return false;
            }

            return EqualsImpl(obj, true);
        }

        public override void ReplaceChild (JSNode oldChild, JSNode newChild) {
            for (int i = 0; i < Arguments.Length; i++)
                if (Arguments[i] == oldChild)
                    Arguments[i] = (JSExpression)newChild;

            base.ReplaceChild(oldChild, newChild);
        }
    }

    public abstract class JSLiteral : JSExpression {
        internal JSLiteral (params JSExpression[] values) : base (values) {
        }

        public abstract object Literal {
            get;
        }

        public static JSTypeNameLiteral New (TypeReference value) {
            return new JSTypeNameLiteral(value);
        }

        public static JSStringLiteral New (string value) {
            return new JSStringLiteral(value);
        }

        public static JSBooleanLiteral New (bool value) {
            return new JSBooleanLiteral(value);
        }

        public static JSCharLiteral New (char value) {
            return new JSCharLiteral(value);
        }

        public static JSIntegerLiteral New (sbyte value) {
            return new JSIntegerLiteral(value, typeof(sbyte));
        }

        public static JSIntegerLiteral New (byte value) {
            return new JSIntegerLiteral(value, typeof(byte));
        }

        public static JSIntegerLiteral New (short value) {
            return new JSIntegerLiteral(value, typeof(short));
        }

        public static JSIntegerLiteral New (ushort value) {
            return new JSIntegerLiteral(value, typeof(ushort));
        }

        public static JSIntegerLiteral New (int value) {
            return new JSIntegerLiteral(value, typeof(int));
        }

        public static JSIntegerLiteral New (uint value) {
            return new JSIntegerLiteral(value, typeof(uint));
        }

        public static JSIntegerLiteral New (long value) {
            return new JSIntegerLiteral(value, typeof(long));
        }

        public static JSIntegerLiteral New (ulong value) {
            return new JSIntegerLiteral((long)value, typeof(ulong));
        }

        public static JSNumberLiteral New (float value) {
            return new JSNumberLiteral(value, typeof(float));
        }

        public static JSNumberLiteral New (double value) {
            return new JSNumberLiteral(value, typeof(double));
        }

        public static JSNumberLiteral New (decimal value) {
            return new JSNumberLiteral((double)value, typeof(decimal));
        }

        public static JSDefaultValueLiteral DefaultValue (TypeReference type) {
            return new JSDefaultValueLiteral(type);
        }

        public static JSNullLiteral Null (TypeReference type) {
            return new JSNullLiteral(type);
        }
    }

    public abstract class JSLiteralBase<T> : JSLiteral {
        public readonly T Value;

        protected JSLiteralBase (T value) {
            Value = value;
        }

        public override object Literal {
            get {
                return this.Value;
            }
        }

        public override bool Equals (object obj) {
            if (obj == null)
                return false;

            if (GetType() != obj.GetType())
                return false;

            var rhs = (JSLiteralBase<T>)obj;
            var comparer = Comparer<T>.Default;

            return comparer.Compare(Value, rhs.Value) == 0;
        }

        public override bool IsConstant {
            get {
                return true;
            }
        }

        public override string ToString () {
            return String.Format("<{0} {1}>", GetType().Name, Value);
        }
    }

    public class JSDefaultValueLiteral : JSLiteralBase<TypeReference> {
        public JSDefaultValueLiteral (TypeReference type)
            : base(type) {
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return Value;
        }
    }

    public class JSNullLiteral : JSLiteralBase<object> {
        public readonly TypeReference Type;

        public JSNullLiteral (TypeReference type)
            : base(null) {

            Type = type;
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            if (Type != null)
                return Type;
            else
                return typeSystem.Object;
        }

        public override string ToString () {
            return "null";
        }
    }

    public class JSBooleanLiteral : JSLiteralBase<bool> {
        public JSBooleanLiteral (bool value)
            : base(value) {
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return typeSystem.Boolean;
        }
    }

    public class JSCharLiteral : JSLiteralBase<char> {
        public JSCharLiteral (char value)
            : base(value) {
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return typeSystem.Char;
        }

        public override string ToString () {
            return Util.EscapeCharacter(Value);
        }
    }

    public class JSStringLiteral : JSLiteralBase<string> {
        public JSStringLiteral (string value)
            : base(value) {
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return typeSystem.String;
        }

        public override string ToString () {
            return Util.EscapeString(Value, '"');
        }
    }

    public class JSIntegerLiteral : JSLiteralBase<long> {
        public readonly Type OriginalType;

        public JSIntegerLiteral (long value, Type originalType)
            : base(value) {

            OriginalType = originalType;
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            if (OriginalType != null) {
                switch (OriginalType.FullName) {
                    case "System.Byte":
                        return typeSystem.Byte;
                    case "System.SByte":
                        return typeSystem.SByte;
                    case "System.UInt16":
                        return typeSystem.UInt16;
                    case "System.Int16":
                        return typeSystem.Int16;
                    case "System.UInt32":
                        return typeSystem.UInt32;
                    case "System.Int32":
                        return typeSystem.Int32;
                    case "System.UInt64":
                        return typeSystem.UInt64;
                    case "System.Int64":
                        return typeSystem.Int64;
                    default:
                        throw new NotImplementedException();
                }
            } else
                return typeSystem.Int64;
        }

        public override string ToString () {
            return String.Format("{0}", Value);
        }
    }

    public class JSEnumLiteral : JSLiteralBase<long> {
        public readonly TypeReference EnumType;
        public readonly string[] Names;

        public JSEnumLiteral (long rawValue, params EnumMemberInfo[] members)
            : base(rawValue) {

            EnumType = members.First().DeclaringType;
            Names = (from m in members select m.Name).ToArray();
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return EnumType;
        }

        public override string ToString () {
            return String.Format("<{0}>", String.Join(
                " | ", (from n in Names select String.Format("{0}.{1}", EnumType.Name, n)).ToArray()
            ));
        }
    }

    public class JSNumberLiteral : JSLiteralBase<double> {
        public readonly Type OriginalType;

        public JSNumberLiteral (double value, Type originalType)
            : base(value) {

                OriginalType = originalType;
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            if (OriginalType != null) {
                switch (OriginalType.FullName) {
                    case "System.Single":
                        return typeSystem.Single;
                    case "System.Double":
                        return typeSystem.Double;
                    case "System.Decimal":
                        return new TypeReference(typeSystem.Double.Namespace, "Decimal", typeSystem.Double.Module, typeSystem.Double.Scope, true);
                    default:
                        throw new NotImplementedException();
                }
            } else
                return typeSystem.Double;
        }
    }

    public class JSTypeNameLiteral : JSLiteralBase<TypeReference> {
        public JSTypeNameLiteral (TypeReference value)
            : base(value) {
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return typeSystem.String;
        }
    }

    public class JSVerbatimLiteral : JSLiteral {
        public readonly TypeReference Type;
        public readonly string Expression;
        public readonly IDictionary<string, JSExpression> Variables;

        public JSVerbatimLiteral (string expression, IDictionary<string, JSExpression> variables, TypeReference type = null)
            : base(GetValues(variables)) {

            Type = type;
            Expression = expression;
            Variables = variables;
        }

        protected static JSExpression[] GetValues (IDictionary<string, JSExpression> variables) {
            if (variables != null)
                return variables.Values.ToArray();
            else
                return new JSExpression[0];
        }

        public override object Literal {
            get { return Expression; }
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            if (Type != null)
                return Type;
            else
                return typeSystem.Object;
        }

        public override void ReplaceChild (JSNode oldChild, JSNode newChild) {
            foreach (var key in Variables.Keys.ToArray()) {
                if (Variables[key] == oldChild)
                    Variables[key] = (JSExpression)newChild;
            }

            base.ReplaceChild(oldChild, newChild);
        }
    }

    public abstract class JSIdentifier : JSExpression {
        public readonly TypeReference Type;

        public JSIdentifier (TypeReference type = null) {
            Type = type;
        }

        public override bool Equals (object obj) {
            var id = obj as JSIdentifier;
            var str = obj as string;

            if (id != null) {
                return String.Equals(Identifier, id.Identifier) &&
                    ILBlockTranslator.TypesAreEqual(Type, id.Type) &&
                    EqualsImpl(obj, true);
            } else {
                return EqualsImpl(obj, true);
            }
        }

        public override int GetHashCode () {
            return Identifier.GetHashCode();
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            if (Type != null)
                return Type;
            else
                return base.GetExpectedType(typeSystem);
        }

        public abstract string Identifier {
            get;
        }

        public override bool IsConstant {
            get {
                return true;
            }
        }

        public override string ToString () {
            return String.Format("<{0} '{1}'>", GetType().Name, Identifier);
        }

        public virtual JSLiteral ToLiteral () {
            return JSLiteral.New(Util.EscapeIdentifier(Identifier));
        }
    }

    public class JSStringIdentifier : JSIdentifier {
        public readonly string Text;

        public JSStringIdentifier (string text, TypeReference type = null)
            : base(type) {
            Text = text;
        }

        public override string Identifier {
            get { return Text; }
        }
    }

    public class JSNamespace : JSStringIdentifier {
        public JSNamespace (string name)
            : base(name) {
        }
    }

    public class JSType : JSIdentifier {
        public readonly TypeReference Type;

        public JSType (TypeReference type) {
            Type = type;
        }

        public override string Identifier {
            get { return Type.FullName; }
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return new TypeReference(
                typeSystem.Boolean.Namespace, "Type",
                typeSystem.Boolean.Module, typeSystem.Boolean.Scope
            );
        }
        
        public override JSLiteral ToLiteral () {
            return JSLiteral.New(Type);
        }
    }

    public class JSField : JSIdentifier {
        public readonly FieldReference Reference;
        public readonly FieldInfo Field;

        public JSField (FieldReference reference, FieldInfo field) {
            if ((reference == null) || (field == null))
                throw new ArgumentNullException();

            Reference = reference;
            Field = field;
        }

        public override string Identifier {
            get { return Field.Name; }
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return TypeAnalysis.SubstituteTypeArgs(Field.ReturnType, Reference);
        }
    }

    public class JSProperty : JSIdentifier {
        public readonly MemberReference Reference;
        public readonly PropertyInfo Property;

        public JSProperty (MemberReference reference, PropertyInfo property) {
            if ((reference == null) || (property == null))
                throw new ArgumentNullException();

            Reference = reference;
            Property = property;
        }

        public override string Identifier {
            get { return Property.Name; }
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return TypeAnalysis.SubstituteTypeArgs(Property.ReturnType, Reference);
        }

        public override bool IsConstant {
            get {
                return false;
            }
        }
    }

    public class JSMethod : JSIdentifier {
        public readonly MethodReference Reference;
        public readonly MethodInfo Method;

        public JSMethod (MethodReference reference, MethodInfo method) {
            if ((reference == null) || (method == null))
                throw new ArgumentNullException();

            if (
                IsOpenGenericType(reference.ReturnType) || reference.Parameters.Any(IsOpenGenericType)
            ) {
                throw new AbortTranslation("Unbound generic type in invocation");
            }

            Reference = reference;
            Method = method;
        }

        public override string Identifier {
            get { return Method.Name; }
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return ConstructDelegateType(Reference, typeSystem);
        }
    }

    public class JSFakeMethod : JSIdentifier {
        public readonly string Name;
        public readonly TypeReference ReturnType;
        public readonly TypeReference[] ParameterTypes;

        public JSFakeMethod (string name, TypeReference returnType, params TypeReference[] parameterTypes) {
            Name = name;
            ReturnType = returnType;
            ParameterTypes = parameterTypes;

            if (IsOpenGenericType(ReturnType))
                throw new OpenGenericTypeException("Open generic return type");
            else if (parameterTypes.Any(IsOpenGenericType))
                throw new OpenGenericTypeException("Open generic parameter type");

            /*
            if (ReturnType.IsGenericParameter || parameterTypes.Any((p) => p.IsGenericParameter))
                throw new ArgumentException()
             */
        }

        public override string Identifier {
            get { return Name; }
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return ConstructDelegateType(
                ReturnType, ParameterTypes, typeSystem
            );
        }
    }

    public class JSVariable : JSIdentifier {
        public readonly string Name;
        protected readonly TypeReference _Type;
        protected readonly bool _IsReference;

        public JSVariable (string name, TypeReference type) {
            Name = name;

            if (type is ByReferenceType) {
                type = ((ByReferenceType)type).ElementType;
                _IsReference = true;
            } else {
                _IsReference = false;
            }

            _Type = type;
        }

        public override string Identifier {
            get { return Name; }
        }

        public virtual TypeReference Type {
            get {
                return _Type;
            }
        }

        public virtual bool IsReference {
            get {
                return _IsReference;
            }
        }

        public virtual bool IsParameter {
            get {
                return false;
            }
        }

        public virtual JSParameter GetParameter () {
            throw new InvalidOperationException();
        }

        public virtual bool IsThis {
            get {
                return false;
            }
        }

        public static JSVariable New (ILVariable variable) {
            return new JSVariable(variable.Name, variable.Type);
        }

        public static JSVariable New (ParameterReference parameter) {
            return new JSParameter(parameter.Name, parameter.ParameterType);
        }

        public virtual JSVariable Reference () {
            return new JSVariableReference(this);
        }

        public virtual JSVariable Dereference () {
            if (IsReference)
                return new JSVariableDereference(this);
            else
                throw new InvalidOperationException();
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return Type;
        }

        public override bool IsConstant {
            get {
                return false;
            }
        }

        public override int GetHashCode () {
            if (Type != null)
                return Identifier.GetHashCode() ^ Type.GetHashCode();
            else
                return Identifier.GetHashCode();
        }

        public override string ToString () {
            if (IsReference)
                return String.Format("<ref {0} {1}>", Type, Identifier);
            else if (IsThis)
                return String.Format("<this {0}>", Type);
            else if (IsParameter)
                return String.Format("<parameter {0} {1}>", Type, Identifier);
            else
                return String.Format("<var {0} {1}>", Type, Identifier);
        }

        public override bool Equals (object obj) {
            var rhs = obj as JSVariable;
            if (rhs != null) {
                if (rhs.Identifier != Identifier)
                    return false;
                if (rhs.IsParameter != IsParameter)
                    return false;
                else if (rhs.IsReference != IsReference)
                    return false;
                else if (rhs.IsThis != IsThis)
                    return false;
                else if (!ILBlockTranslator.TypesAreEqual(Type, rhs.Type))
                    return false;
                else
                    return true;
            }

            return EqualsImpl(obj, true);
        }
    }

    public class JSParameter : JSVariable {
        internal JSParameter (string name, TypeReference type)
            : base(name, type) {
        }

        public override bool IsParameter {
            get {
                return true;
            }
        }

        public override JSParameter GetParameter () {
            return this;
        }
    }

    public class JSExceptionVariable : JSVariable {
        public JSExceptionVariable (TypeSystem typeSystem) :
            base("$exception", new TypeReference("System", "Exception", typeSystem.Object.Module, typeSystem.Object.Scope)) {
        }

        public override bool IsConstant {
            get {
                return true;
            }
        }
    }

    public class JSThisParameter : JSParameter {
        public JSThisParameter (TypeReference type) : 
            base("this", type) {
        }

        public override bool IsThis {
            get {
                return true;
            }
        }

        public override bool IsConstant {
            get {
                return true;
            }
        }

        public static JSVariable New (TypeReference type) {
            if (type.IsValueType)
                return new JSVariableReference(new JSThisParameter(type));
            else
                return new JSThisParameter(type);
        }
    }

    public class JSVariableDereference : JSVariable {
        public readonly JSVariable Referent;

        public JSVariableDereference (JSVariable referent)
            : base(referent.Identifier, null) {

            Referent = referent;
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return DeReferenceType(Referent.GetExpectedType(typeSystem), true);
        }

        public override TypeReference Type {
            get {
                return DeReferenceType(Referent.Type, true);
            }
        }

        public override bool IsReference {
            get {
                return DeReferenceType(Referent.Type, true) is ByReferenceType;
            }
        }

        public override bool IsParameter {
            get {
                return Referent.IsParameter;
            }
        }

        public override JSParameter GetParameter () {
            return Referent.GetParameter();
        }

        public override bool IsThis {
            get {
                return Referent.IsThis;
            }
        }

        public override JSVariable Reference () {
            return Referent;
        }
    }

    public class JSIndirectVariable : JSVariable {
        public readonly IDictionary<string, JSVariable> Variables;

        public JSIndirectVariable (IDictionary<string, JSVariable> variables, string identifier)
            : base(identifier, null) {

            Variables = variables;
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return Variables[Identifier].GetExpectedType(typeSystem);
        }

        public override TypeReference Type {
            get {
                return Variables[Identifier].Type;
            }
        }

        public override bool IsReference {
            get {
                return Variables[Identifier].IsReference;
            }
        }

        public override bool IsParameter {
            get {
                return Variables[Identifier].IsParameter;
            }
        }

        public override JSParameter GetParameter () {
            return Variables[Identifier].GetParameter();
        }

        public override bool IsConstant {
            get {
                return Variables[Identifier].IsConstant;
            }
        }

        public override bool IsThis {
            get {
                return Variables[Identifier].IsThis;
            }
        }

        public override JSVariable Dereference () {
            return Variables[Identifier].Dereference();
        }

        public override JSVariable Reference () {
            return Variables[Identifier].Reference();
        }

        public override bool Equals (object obj) {
            JSVariable variable;
            if (Variables.TryGetValue(Identifier, out variable))
                return variable.Equals(obj) || base.Equals(obj);
            else {
                variable = obj as JSVariable;
                if (variable.Identifier == Identifier)
                    return true;
                else
                    return base.Equals(obj);
            }
        }

        public override string ToString () {
            JSVariable variable;
            if (Variables.TryGetValue(Identifier, out variable))
                return String.Format("@{0}", variable);
            else
                return "@undef";
        }
    }

    public class JSVariableReference : JSVariable {
        public readonly JSVariable Referent;

        public JSVariableReference (JSVariable referent)
            : base(referent.Identifier, null) {

            Referent = referent;
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return new ByReferenceType(Referent.GetExpectedType(typeSystem));
        }

        public override TypeReference Type {
            get {
                return new ByReferenceType(Referent.Type);
            }
        }

        public override bool IsReference {
            get {
                return true;
            }
        }

        public override JSParameter GetParameter () {
            return Referent.GetParameter();
        }

        public override bool IsParameter {
            get {
                return Referent.IsParameter;
            }
        }

        public override bool IsThis {
            get {
                return Referent.IsThis;
            }
        }

        public override JSVariable Dereference () {
            return Referent;
        }
    }

    public class JSDotExpression : JSExpression {
        public JSDotExpression (JSExpression target, JSIdentifier member)
            : base (target, member) {

            if ((target == null) || (member == null))
                throw new ArgumentNullException();
        }

        public static JSDotExpression New (JSExpression leftMost, params JSIdentifier[] memberNames) {
            if ((memberNames == null) || (memberNames.Length == 0))
                throw new ArgumentException("memberNames");

            var result = new JSDotExpression(leftMost, memberNames[0]);
            for (var i = 1; i < memberNames.Length; i++)
                result = new JSDotExpression(result, memberNames[i]);

            return result;
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return Member.GetExpectedType(typeSystem);
        }

        public JSExpression Target {
            get {
                return Values[0];
            }
        }

        public JSIdentifier Member {
            get {
                return (JSIdentifier)Values[1];
            }
        }

        public override bool IsConstant {
            get {
                return Target.IsConstant && Member.IsConstant;
            }
        }

        public override string ToString () {
            return String.Format("{0}.{1}", Target, Member);
        }
    }

    public class JSIndexerExpression : JSExpression {
        public TypeReference ElementType;

        public JSIndexerExpression (JSExpression target, JSExpression index, TypeReference elementType = null)
            : base (target, index) {

            ElementType = elementType;
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            if (ElementType != null)
                return ElementType;

            var targetType = DeReferenceType(Target.GetExpectedType(typeSystem));

            var at = targetType as ArrayType;
            if (at != null)
                return at.ElementType;
            else
                return base.GetExpectedType(typeSystem);
        }

        public JSExpression Target {
            get {
                return Values[0];
            }
        }

        public JSExpression Index {
            get {
                return Values[1];
            }
        }

        public override bool IsConstant {
            get {
                return Target.IsConstant && Index.IsConstant;
            }
        }

        public override string ToString () {
            return String.Format("{0}[{1}]", Target, Index);
        }
    }

    public class JSNewExpression : JSExpression {
        public readonly MethodInfo Constructor;

        public JSNewExpression (TypeReference type, MethodInfo constructor, params JSExpression[] arguments)
            : this (new JSType(type), constructor, arguments) {
        }

        public JSNewExpression (JSExpression type, MethodInfo constructor, params JSExpression[] arguments) : base(
            (new [] { type }).Concat(arguments).ToArray()
        ) {
            Constructor = constructor;
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            var type = Type as JSType;

            if (type != null)
                return type.Type;
            else if (Constructor != null)
                return Constructor.DeclaringType.Definition;
            else
                return typeSystem.Object;
        }

        public JSExpression Type {
            get {
                return Values[0];
            }
        }

        public IList<JSExpression> Arguments {
            get {
                return Values.Skip(1);
            }
        }
    }

    public class JSInvocationExpression : JSExpression {
        public bool ConstantIfArgumentsAre = false;

        public JSInvocationExpression (JSExpression target, params JSExpression[] arguments)
            : base ( 
                (new [] { target }).Concat(arguments).ToArray() 
            ) {
        }

        public JSExpression Target {
            get {
                return Values[0];
            }
        }

        public override bool IsConstant {
            get {
                if (ConstantIfArgumentsAre)
                    return Arguments.All((a) => a.IsConstant) || base.IsConstant;
                else
                    return base.IsConstant;
            }
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            var targetType = Target.GetExpectedType(typeSystem);

            var targetAbstractMethod = Target.AllChildrenRecursive.OfType<JSIdentifier>()
                .LastOrDefault((i) => {
                    var m = i as JSMethod;
                    var fm = i as JSFakeMethod;
                    return (m != null) || (fm != null);
                });

            var targetMethod = (Target as JSMethod) ?? (targetAbstractMethod as JSMethod);
            var targetFakeMethod = (Target as JSFakeMethod) ?? (targetAbstractMethod as JSFakeMethod);

            if (targetMethod != null)
                return TypeAnalysis.SubstituteTypeArgs(targetMethod.Reference.ReturnType, targetMethod.Reference);
            else if (targetFakeMethod != null)
                return targetFakeMethod.ReturnType;

            // Any invocation expression targeting a method or delegate will have an expected type that is a delegate.
            // This should be handled by replacing the JSInvocationExpression with a JSDelegateInvocationExpression
            if (ILBlockTranslator.IsDelegateType(targetType))
                throw new NotImplementedException();                

            return targetType;
        }

        public virtual IList<JSExpression> Arguments {
            get {
                return Values.Skip(1);
            }
        }

        public override string ToString () {
            return String.Format(
                "{0}:({1})", 
                Target, 
                String.Join(", ", (from a in Arguments select String.Concat(a)).ToArray())
            );
        }
    }

    public class JSDelegateInvocationExpression : JSExpression {
        public JSDelegateInvocationExpression (JSExpression target, JSMethod invokeMethod, params JSExpression[] arguments)
            : base ( 
                (new [] { target, invokeMethod }).Concat(arguments).ToArray() 
            ) {
        }

        public JSExpression Target {
            get {
                return Values[0];
            }
        }

        public JSMethod InvokeMethod {
            get {
                return (JSMethod)Values[1];
            }
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {            
            return TypeAnalysis.SubstituteTypeArgs(InvokeMethod.Reference.ReturnType, InvokeMethod.Reference);
        }

        public virtual IList<JSExpression> Arguments {
            get {
                return Values.Skip(2);
            }
        }

        public override string ToString () {
            return String.Format(
                "{0}:({1})", 
                Target, 
                String.Join(", ", (from a in Arguments select String.Concat(a)).ToArray())
            );
        }
    }

    public class JSArrayExpression : JSExpression {
        public readonly TypeReference ElementType;

        public JSArrayExpression (TypeReference elementType, params JSExpression[] values)
            : base (values) {

            ElementType = elementType;
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            if (ElementType != null)
                return new ArrayType(ElementType);
            else
                return base.GetExpectedType(typeSystem);
        }

        new public IEnumerable<JSExpression> Values {
            get {
                return base.Values;
            }
        }

        public override bool IsConstant {
            get {
                return Values.All((v) => v.IsConstant);
            }
        }
    }

    public class JSPairExpression : JSExpression {
        public JSPairExpression (JSExpression key, JSExpression value)
            : base (key, value) {
        }

        public JSExpression Key {
            get {
                return Values[0];
            }
            set {
                Values[0] = value;
            }
        }

        public JSExpression Value {
            get {
                return Values[1];
            }
            set {
                Values[1] = value;
            }
        }
    }

    public class JSObjectExpression : JSExpression {
        public JSObjectExpression (params JSPairExpression[] pairs) : base(
            pairs
        ) {
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return typeSystem.Object;
        }

        new public IEnumerable<JSPairExpression> Values {
            get {
                return from v in base.Values select (JSPairExpression)v;
            }
        }
    }

    public abstract class JSOperatorExpression<TOperator> : JSExpression
        where TOperator : JSOperator {

        public readonly TOperator Operator;
        public readonly TypeReference ExpectedType;

        protected JSOperatorExpression (TOperator op, TypeReference expectedType, params JSExpression[] values)
            : base(values) {

            Operator = op;
            ExpectedType = expectedType;
        }

        public override bool IsConstant {
            get {
                return Values.All((v) => v.IsConstant);
            }
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            if (ExpectedType != null)
                return ExpectedType;

            TypeReference inferredType = null;
            foreach (var value in Values) {
                var valueType = value.GetExpectedType(typeSystem);

                if (inferredType == null)
                    inferredType = valueType;
                else if (valueType.FullName == inferredType.FullName)
                    continue;
                else
                    return base.GetExpectedType(typeSystem);
            }

            return inferredType;
        }
    }

    public class JSTernaryOperatorExpression : JSExpression {
        public readonly TypeReference ExpectedType;

        public JSTernaryOperatorExpression (JSExpression condition, JSExpression trueValue, JSExpression falseValue, TypeReference expectedType)
            : base (condition, trueValue, falseValue) {

            ExpectedType = expectedType;
        }

        public JSExpression Condition {
            get {
                return Values[0];
            }
        }

        public JSExpression True{
            get {
                return Values[1];
            }
        }

        public JSExpression False {
            get {
                return Values[2];
            }
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return ExpectedType;
        }
    }

    public class JSBinaryOperatorExpression : JSOperatorExpression<JSBinaryOperator> {
        /// <summary>
        /// Construct a binary operator expression with an explicit expected type.
        /// If the explicit expected type is null, expected type will be inferred to be the type of both sides if they share a type.
        /// </summary>
        public JSBinaryOperatorExpression (JSBinaryOperator op, JSExpression lhs, JSExpression rhs, TypeReference expectedType) : base(
            op, expectedType, lhs, rhs
        ) {
        }

        public JSExpression Left {
            get {
                return Values[0];
            }
            set {
                Values[0] = value;
            }
        }

        public JSExpression Right {
            get {
                return Values[1];
            }
            set {
                Values[1] = value;
            }
        }

        public static JSBinaryOperatorExpression New (JSBinaryOperator op, IList<JSExpression> values, TypeReference expectedType) {
            if (values.Count < 2)
                throw new ArgumentException();

            var result = new JSBinaryOperatorExpression(
                op, values[0], values[1], expectedType
            );
            var current = result;

            for (int i = 2, c = values.Count; i < c; i++) {
                var next = new JSBinaryOperatorExpression(op, current.Right, values[i], expectedType);
                current.Right = next;
                current = next;
            }

            return result;
        }

        public override string ToString () {
            return String.Format("({0} {1} {2})", Left, Operator, Right);
        }
    }

    public class JSUnaryOperatorExpression : JSOperatorExpression<JSUnaryOperator> {
        public JSUnaryOperatorExpression (JSUnaryOperator op, JSExpression expression, TypeReference expectedType = null)
            : base(op, expectedType, expression) {
        }

        public bool IsPostfix {
            get {
                return ((JSUnaryOperator)Operator).IsPostfix;
            }
        }

        public JSExpression Expression {
            get {
                return Values[0];
            }
        }

        public override string ToString () {
            if (IsPostfix)
                return String.Format("({0}{1})", Expression, Operator);
            else
                return String.Format("({0}{1})", Operator, Expression);
        }
    }

    public class JSChangeTypeExpression : JSExpression {
        public readonly TypeReference NewType;

        protected JSChangeTypeExpression (JSExpression inner, TypeReference newType)
            : base(inner) {

            NewType = newType;
        }

        public static JSExpression New (JSExpression inner, TypeSystem typeSystem, TypeReference newType) {
            var cte = inner as JSChangeTypeExpression;
            JSChangeTypeExpression result;

            if (cte != null) {
                inner = cte.Expression;

                result = new JSChangeTypeExpression(cte.Expression, newType);
            } else {
                result = new JSChangeTypeExpression(inner, newType);
            }

            var innerType = inner.GetExpectedType(typeSystem);
            if (ILBlockTranslator.TypesAreEqual(newType, innerType))
                return inner;
            else
                return result;
        }

        public JSExpression Expression {
            get {
                return Values[0];
            }
        }

        public override bool IsConstant {
            get {
                return Expression.IsConstant;
            }
        }

        public override bool IsNull {
            get {
                return Expression.IsNull;
            }
        }

        public override TypeReference GetExpectedType (TypeSystem typeSystem) {
            return NewType;
        }
    }

    public class NoExpectedTypeException : NotImplementedException {
        public NoExpectedTypeException (JSExpression node)
            : base(String.Format("Node of type {0} has no expected type: {1}", node.GetType().Name, node)) {
        }
    }
}
