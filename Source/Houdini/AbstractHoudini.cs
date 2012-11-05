﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Boogie;
using Microsoft.Boogie.VCExprAST;
using VC;
using Outcome = VC.VCGen.Outcome;
using Bpl = Microsoft.Boogie;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using Microsoft.Boogie.GraphUtil;

namespace Microsoft.Boogie.Houdini {

    public class AbstractHoudini
    {
        // Input Program
        Program program;
        // Impl -> VC
        Dictionary<string, VCExpr> impl2VC;
        // Impl -> Vars at end of the impl
        Dictionary<string, List<VCExpr>> impl2EndStateVars;
        // Impl -> (callee,summary pred)
        Dictionary<string, List<Tuple<string, VCExprNAry>>> impl2CalleeSummaries;
        // pointer to summary class
        ISummaryElement summaryClass;
        // impl -> summary
        Dictionary<string, ISummaryElement> impl2Summary;
        // name -> impl
        Dictionary<string, Implementation> name2Impl;

        public static readonly string summaryPredSuffix = "SummaryPred";

        // Essentials: VCGen, Prover, and reporter
        VCGen vcgen;
        ProverInterface prover;
        AbstractHoudiniErrorReporter reporter;

        public AbstractHoudini(Program program)
        {
            this.program = program;
            this.impl2VC = new Dictionary<string, VCExpr>();
            this.impl2EndStateVars = new Dictionary<string, List<VCExpr>>();
            this.impl2CalleeSummaries = new Dictionary<string, List<Tuple<string, VCExprNAry>>>();
            this.impl2Summary = new Dictionary<string, ISummaryElement>();
            this.name2Impl = BoogieUtil.nameImplMapping(program);

            this.vcgen = new VCGen(program, CommandLineOptions.Clo.SimplifyLogFilePath, CommandLineOptions.Clo.SimplifyLogFileAppend);
            this.prover = ProverInterface.CreateProver(program, CommandLineOptions.Clo.SimplifyLogFilePath, CommandLineOptions.Clo.SimplifyLogFileAppend, CommandLineOptions.Clo.ProverKillTime);
            this.reporter = new AbstractHoudiniErrorReporter();

            var impls = new List<Implementation>(
                program.TopLevelDeclarations.OfType<Implementation>());

            // Create all VCs
            impls
                .Iter(attachEnsures);

            impls
                .Iter(GenVC);

        }

        public void computeSummaries(ISummaryElement summaryClass)
        {
            this.summaryClass = summaryClass;

            var main = program.TopLevelDeclarations
                .OfType<Implementation>()
                .Where(impl => QKeyValue.FindBoolAttribute(impl.Attributes, "entrypoint"))
                .FirstOrDefault();

            Debug.Assert(main != null);

            program.TopLevelDeclarations
                .OfType<Implementation>()
                .Iter(impl => impl2Summary.Add(impl.Name, summaryClass.GetFlaseSummary(program, impl)));

            // Build call graph
            var Succ = new Dictionary<Implementation, HashSet<Implementation>>();
            var Pred = new Dictionary<Implementation, HashSet<Implementation>>();
            name2Impl.Values.Iter(impl => Succ.Add(impl, new HashSet<Implementation>()));
            name2Impl.Values.Iter(impl => Pred.Add(impl, new HashSet<Implementation>()));

            foreach (var impl in program.TopLevelDeclarations.OfType<Implementation>())
            {
                foreach (var blk in impl.Blocks)
                {
                    foreach (var cmd in blk.Cmds.OfType<CallCmd>())
                    {
                        if (!name2Impl.ContainsKey(cmd.callee)) continue;
                        Succ[impl].Add(name2Impl[cmd.callee]);
                        Pred[name2Impl[cmd.callee]].Add(impl);
                    }
                }
            }

            // Build SCC
            var sccs = new StronglyConnectedComponents<Implementation>(name2Impl.Values,
                new Adjacency<Implementation>(n => Succ[n]),
                new Adjacency<Implementation>(n => Pred[n]));
            sccs.Compute();

            // impl -> priority
            var impl2Priority = new Dictionary<string, int>();
            int p = 0;
            foreach (var scc in sccs)
            {
                scc.Iter(n => impl2Priority.Add(n.Name, p));
                p++;
            }

            var worklist = new SortedSet<Tuple<int, Implementation>>();
            name2Impl.Values
                .Iter(impl => worklist.Add(Tuple.Create(impl2Priority[impl.Name], impl)));

            while (worklist.Any())
            {
                var impl = worklist.First().Item2;
                worklist.Remove(worklist.First());

                var changed = ProcessImpl(impl);

                if (changed)
                {
                    Pred[impl].Iter(pred => worklist.Add(Tuple.Create(impl2Priority[pred.Name], pred)));
                }
            }

            foreach (var tup in impl2Summary)
            {
                Console.WriteLine("Summary of {0}:", tup.Key);
                Console.WriteLine("{0}", tup.Value);
            }

            prover.Close();
            CommandLineOptions.Clo.TheProverFactory.Close();
        }

        private bool ProcessImpl(Implementation impl)
        {
            var ret = false;
            var gen = prover.VCExprGen;

            // construct summaries
            var env = VCExpressionGenerator.True;
            foreach (var tup in impl2CalleeSummaries[impl.Name])
            {
                if (tup.Item1 == impl.Name)
                    continue;

                var calleeSummary = 
                    impl2Summary[tup.Item1].GetSummaryExpr(
                       GetVarMapping(name2Impl[tup.Item1], tup.Item2), prover.VCExprGen);
                env = gen.AndSimp(env, gen.Eq(tup.Item2, calleeSummary));
            }

            while(true)
            {
                // construct self summaries
                var summaryExpr = VCExpressionGenerator.True;
                foreach (var tup in impl2CalleeSummaries[impl.Name])
                {
                    if (tup.Item1 != impl.Name)
                        continue;

                    var ts =
                        impl2Summary[tup.Item1].GetSummaryExpr(
                           GetVarMapping(name2Impl[tup.Item1], tup.Item2), prover.VCExprGen);
                    summaryExpr = gen.AndSimp(summaryExpr, gen.Eq(tup.Item2, ts));
                }
                Console.WriteLine("Trying summary for {0}: {1}", impl.Name, summaryExpr);

                reporter.model = null;
                var vc = gen.AndSimp(env, summaryExpr);
                vc = gen.Implies(vc, impl2VC[impl.Name]);
                
                //Console.WriteLine("Checking: {0}", vc);

                prover.BeginCheck(impl.Name, vc, reporter);
                ProverInterface.Outcome proverOutcome = prover.CheckOutcome(reporter);
                if (reporter.model == null)
                    break;
                
                var state = CollectState(impl);
                impl2Summary[impl.Name].Join(state);
                ret = true;
            }
            return ret;
        }

        private Dictionary<string, VCExpr> GetVarMapping(Implementation impl, VCExprNAry summaryPred)
        {
            var ret = new Dictionary<string, VCExpr>();

            var cnt = 0;
            foreach (var g in program.TopLevelDeclarations.OfType<GlobalVariable>())
            {
                ret.Add(string.Format("old({0})", g.Name), summaryPred[cnt]);
                cnt++;
            }
            foreach (var v in impl.InParams.OfType<Variable>().Concat(
                impl.OutParams.OfType<Variable>().Concat(
                impl.Proc.Modifies.OfType<IdentifierExpr>().Select(ie => ie.Decl))))
            {
                ret.Add(v.Name, summaryPred[cnt]);
                cnt++;
            }

            return ret;

        }

        private Dictionary<string, Model.Element> CollectState(Implementation impl)
        {
            var ret = new Dictionary<string, Model.Element>();

            var model = reporter.model;
            var implVars = impl2EndStateVars[impl.Name];

            var cnt = 0;
            foreach (var g in program.TopLevelDeclarations.OfType<GlobalVariable>())
            {
                ret.Add(string.Format("old({0})", g.Name), getValue(implVars[cnt], model));
                cnt++;
            }
            foreach (var v in impl.InParams.OfType<Variable>().Concat(
                impl.OutParams.OfType<Variable>().Concat(
                impl.Proc.Modifies.OfType<IdentifierExpr>().Select(ie => ie.Decl))))
            {
                ret.Add(v.Name, getValue(implVars[cnt], model));
                cnt++;
            }

            return ret;
        }

        private Model.Element getValue(VCExpr arg, Model model)
        {
            if (arg is VCExprLiteral)
            {
                return model.GetElement(arg.ToString());
            }
            else if (arg is VCExprVar)
            {
                var el = model.GetFunc(prover.Context.Lookup(arg as VCExprVar));
                Debug.Assert(el.Arity == 0 && el.AppCount == 1);
                return el.Apps.First().Result;
            }
            else
            {
                Debug.Assert(false);
                return null;
            }
        }

        private void attachEnsures(Implementation impl)
        {
            VariableSeq functionInterfaceVars = new VariableSeq();
            foreach (Variable v in vcgen.program.GlobalVariables())
            {
                functionInterfaceVars.Add(new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "", v.TypedIdent.Type), true));
            }
            foreach (Variable v in impl.InParams)
            {
                functionInterfaceVars.Add(new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "", v.TypedIdent.Type), true));
            }
            foreach (Variable v in impl.OutParams)
            {
                functionInterfaceVars.Add(new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "", v.TypedIdent.Type), true));
            }
            foreach (IdentifierExpr e in impl.Proc.Modifies)
            {
                if (e.Decl == null) continue;
                functionInterfaceVars.Add(new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "", e.Decl.TypedIdent.Type), true));
            }
            Formal returnVar = new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "", Bpl.Type.Bool), false);
            var function = new Function(Token.NoToken, impl.Name + summaryPredSuffix, functionInterfaceVars, returnVar);
            prover.Context.DeclareFunction(function, "");

            ExprSeq exprs = new ExprSeq();
            foreach (Variable v in vcgen.program.GlobalVariables())
            {
                Contract.Assert(v != null);
                exprs.Add(new OldExpr(Token.NoToken, new IdentifierExpr(Token.NoToken, v)));
            }
            foreach (Variable v in impl.Proc.InParams)
            {
                Contract.Assert(v != null);
                exprs.Add(new IdentifierExpr(Token.NoToken, v));
            }
            foreach (Variable v in impl.Proc.OutParams)
            {
                Contract.Assert(v != null);
                exprs.Add(new IdentifierExpr(Token.NoToken, v));
            }
            foreach (IdentifierExpr ie in impl.Proc.Modifies)
            {
                Contract.Assert(ie != null);
                if (ie.Decl == null)
                    continue;
                exprs.Add(ie);
            }
            Expr postExpr = new NAryExpr(Token.NoToken, new FunctionCall(function), exprs);
            impl.Proc.Ensures.Add(
                new Ensures(Token.NoToken, false, postExpr, ""));
        }

        private void GenVC(Implementation impl)
        {
            ModelViewInfo mvInfo;
            System.Collections.Hashtable label2absy;

            vcgen.ConvertCFG2DAG(impl);
            vcgen.PassifyImpl(impl, out mvInfo);

            var gen = prover.VCExprGen;
            var vcexpr = vcgen.GenerateVC(impl, null, out label2absy, prover.Context);

            // Create a macro so that the VC can sit with the theorem prover
            Macro macro = new Macro(Token.NoToken, impl.Name + "Macro", new VariableSeq(), new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "", Bpl.Type.Bool), false));
            prover.DefineMacro(macro, vcexpr);

            // Store VC
            impl2VC.Add(impl.Name, gen.Function(macro));

            //Console.WriteLine("VC of {0}: {1}", impl.Name, vcexpr);

            // Find the assert
            impl2EndStateVars.Add(impl.Name, new List<VCExpr>());
            var found = false;
            foreach (var blk in impl.Blocks)
            {
                foreach (var cmd in blk.Cmds.OfType<AssertCmd>())
                {
                    if (BoogieUtil.isAssertTrue(cmd)) continue;
                    var nary = cmd.Expr as NAryExpr;
                    if (nary == null) continue;
                    var pred = nary.Fun as FunctionCall;
                    if (pred == null || pred.FunctionName != (impl.Name + (AbstractHoudini.summaryPredSuffix)))
                        continue;

                    Debug.Assert(!found);
                    found = true;
                    nary.Args.OfType<Expr>()
                        .Iter(expr => impl2EndStateVars[impl.Name].Add(prover.Context.BoogieExprTranslator.Translate(expr)));
                }
            }
            Debug.Assert(found);

            // Grab summary predicates
            var visitor = new FindSummaryPred(prover.VCExprGen);
            visitor.Mutate(vcexpr, true);

            impl2CalleeSummaries.Add(impl.Name, new List<Tuple<string, VCExprNAry>>());
            visitor.summaryPreds.Iter(tup => impl2CalleeSummaries[impl.Name].Add(tup));
        }
    }

    public interface ISummaryElement
    {
        ISummaryElement GetFlaseSummary(Program program, Implementation impl);
        void Join(Dictionary<string, Model.Element> state);
        VCExpr GetSummaryExpr(Dictionary<string, VCExpr> incarnations, VCExpressionGenerator gen);
    }

    public class ConstantVal : ISummaryElement
    {
        Program program;
        Implementation impl;
        // var -> const set
        Dictionary<string, HashSet<int>> val;
        // set of vars
        HashSet<Variable> vars;

        public static readonly int MAX = 3;

        public ConstantVal()
        {
            // this is just a place holder
            val = new Dictionary<string, HashSet<int>>();
            vars = new HashSet<Variable>();
        }

        private ConstantVal(Program program, Implementation impl)
        {
            this.program = program;
            this.impl = impl;
            this.val = new Dictionary<string, HashSet<int>>();

            vars = new HashSet<Variable>();
            impl.Proc.Modifies
                .OfType<IdentifierExpr>()
                .Select(ie => ie.Decl)
                .Where(v => v.TypedIdent.Type.IsInt)
                .Iter(v => vars.Add(v));
            impl.OutParams.OfType<Variable>()
                .Where(v => v.TypedIdent.Type.IsInt)
                .Iter(v => vars.Add(v));

            vars.Iter(v => val.Add(v.Name, null));
        }


        public void Join(Dictionary<string, Model.Element> state)
        {
            foreach (var vv in vars)
            {
                var v = vv.Name;
                var newv = state[v].AsInt();
                var oldv = val[v];

                if (oldv == null)
                {
                    val[v] = new HashSet<int>();
                    val[v].Add(newv);
                }
                else if(oldv.Count > 0)
                {
                    val[v].Add(newv);
                    if (val[v].Count > MAX)
                        val[v] = new HashSet<int>();
                } 

            }
        }

        public VCExpr GetSummaryExpr(Dictionary<string, VCExpr> incarnations, VCExpressionGenerator gen)
        {
            VCExpr ret = VCExpressionGenerator.True;
            if (val.Values.Any(v => v == null))
                return VCExpressionGenerator.False;
            
            foreach (var v in vars)
            {
                var consts = val[v.Name];
                Debug.Assert(consts != null);

                if (consts.Count == 0)
                    continue;

                var vexpr = VCExpressionGenerator.False;
                consts.Iter(c => vexpr = gen.OrSimp(vexpr, gen.Eq(incarnations[v.Name], gen.Integer(Microsoft.Basetypes.BigNum.FromInt(c)))));
                ret = gen.AndSimp(ret, vexpr);
            }

            return ret;
        }

        public override string ToString()
        {
            var ret = "true";
            if (val.Values.Any(v => v == null))
                return "false";

            foreach (var v in vars)
            {
                var consts = val[v.Name];
                Debug.Assert(consts != null);

                if (consts.Count == 0)
                    continue;

                var vexpr = "false";
                consts.Iter(c => vexpr = 
                    string.Format("{0} OR ({1} == {2})", vexpr, v.Name, c));

                ret = string.Format("{0} AND ({1})", ret, vexpr);
            }

            return ret;
        }


        public ISummaryElement GetFlaseSummary(Program program, Implementation impl)
        {
            return new ConstantVal(program, impl);
        }
    }

    public class PredicateAbs : ISummaryElement
    {
        public static List<Expr> PrePreds { get; private set; }
        public static List<Expr> PostPreds { get; private set; }

        PredicateAbsDisjunct[] value;
        bool isFalse;

        public PredicateAbs()
        {
            isFalse = true;
            value = new PredicateAbsDisjunct[PostPreds.Count];
            for (int i = 0; i < PostPreds.Count; i++) value[i] = null;
        }

        public static void Initialize(Program program)
        {
            PrePreds = new List<Expr>();
            PostPreds = new List<Expr>(); 

            foreach (var proc in
                program.TopLevelDeclarations.OfType<Procedure>()
                .Where(proc => QKeyValue.FindBoolAttribute(proc.Attributes, "template")))
            {
                foreach (var ens in proc.Ensures.OfType<Ensures>())
                {
                    if (QKeyValue.FindBoolAttribute(ens.Attributes, "pre"))
                        PrePreds.Add(ens.Condition);
                    else
                        PostPreds.Add(ens.Condition);
                }
            }

            Console.WriteLine("Running Abstract Houdini");
            PostPreds.Iter(expr => Console.WriteLine("\tPost: {0}", expr));
            PrePreds.Iter(expr => Console.WriteLine("\tPre: {0}", expr));
        }

        private object Eval(Expr expr, Dictionary<string, Model.Element> state)
        {
            if (expr is LiteralExpr)
            {
                return (expr as LiteralExpr).Val;
            }

            if (expr is IdentifierExpr)
            {
                return LookupVariable((expr as IdentifierExpr).Name, state, false);
            }

            if (expr is OldExpr)
            {
                var ide = (expr as OldExpr).Expr as IdentifierExpr;
                Debug.Assert(ide != null);

                return LookupVariable(ide.Name, state, true);
            }

            if (expr is NAryExpr)
            {
                var nary = expr as NAryExpr;
                if (nary.Fun is UnaryOperator)
                {
                    return (nary.Fun as UnaryOperator).Evaluate(Eval(nary.Args[0], state));
                }
                if (nary.Fun is BinaryOperator)
                {
                    return (nary.Fun as BinaryOperator).Evaluate(Eval(nary.Args[0], state), Eval(nary.Args[1], state));
                }
                Debug.Assert(false, "No other op is handled");                
            }
            throw new NotImplementedException(string.Format("Expr of type {0} is not handled", expr.GetType().ToString()));
        }

        private object LookupVariable(string v, Dictionary<string, Model.Element> state, bool tryOld)
        {
            if (tryOld)
            {
                var oldv = string.Format("old({0})", v);
                if (state.ContainsKey(oldv))
                {
                    return ToValue(state[oldv]);
                }
                throw new InternalError("Cannot handle this case");
            }

            if (state.ContainsKey(v))
            {
                return ToValue(state[v]);
            }

            throw new InternalError("Cannot handle this case");
        }

        private VCExpr ToVcVar(string v, Dictionary<string, VCExpr> incarnations, bool tryOld)
        {
            if (tryOld)
            {
                var oldv = string.Format("old({0})", v);
                if (incarnations.ContainsKey(oldv))
                {
                    return incarnations[oldv];
                }
                throw new InternalError("Cannot handle this case");
            }

            if (incarnations.ContainsKey(v))
            {
                return incarnations[v];
            }

            throw new InternalError("Cannot handle this case");
        }

        private object ToValue(Model.Element elem)
        {
            if (elem is Model.Integer)
            {
                return Microsoft.Basetypes.BigNum.FromInt((elem as Model.Integer).AsInt());
            }
            if (elem is Model.Boolean)
            {
                return (elem as Model.Boolean).Value;
            }
            throw new NotImplementedException("Cannot yet handle this Model.Element type");
        }

        private VCExpr ToVcExpr(Expr expr, Dictionary<string, VCExpr> incarnations, VCExpressionGenerator gen)
        {
            if (expr is LiteralExpr)
            {
                var val = (expr as LiteralExpr).Val;
                if (val is bool)
                {
                    if ((bool)val)
                    {
                        return VCExpressionGenerator.True;
                    }
                    else
                    {
                        return VCExpressionGenerator.False;
                    }
                }
                else if (val is Microsoft.Basetypes.BigNum)
                {
                    return gen.Integer((Microsoft.Basetypes.BigNum)val);
                }

                throw new NotImplementedException("Cannot handle literals of this type");
            }

            if (expr is IdentifierExpr)
            {
                return ToVcVar((expr as IdentifierExpr).Name, incarnations, false);
            }

            if (expr is OldExpr)
            {
                var ide = (expr as OldExpr).Expr as IdentifierExpr;
                Debug.Assert(ide != null);

                return ToVcVar(ide.Name, incarnations, true);
            }

            if (expr is NAryExpr)
            {
                var nary = expr as NAryExpr;
                if (nary.Fun is UnaryOperator)
                {
                    Debug.Assert((nary.Fun as UnaryOperator).Op == UnaryOperator.Opcode.Not);
                    return gen.Not(ToVcExpr(nary.Args[0], incarnations, gen));
                }
                if (nary.Fun is BinaryOperator)
                {
                    return gen.Function(Translate(nary.Fun as BinaryOperator), ToVcExpr(nary.Args[0], incarnations, gen), ToVcExpr(nary.Args[1], incarnations, gen));
                }
                Debug.Assert(false, "No other op is handled");
            }
            throw new NotImplementedException(string.Format("Expr of type {0} is not handled", expr.GetType().ToString()));
        }

        private VCExprOp Translate(BinaryOperator op)
        {
            switch (op.Op)
            {
                case BinaryOperator.Opcode.Add:
                    return VCExpressionGenerator.AddIOp;
                case BinaryOperator.Opcode.Sub:
                    return VCExpressionGenerator.SubIOp;
                case BinaryOperator.Opcode.Mul:
                    return VCExpressionGenerator.MulIOp;
                case BinaryOperator.Opcode.Div:
                    return VCExpressionGenerator.DivIOp;
                case BinaryOperator.Opcode.Mod:
                    return VCExpressionGenerator.ModOp;
                case BinaryOperator.Opcode.Eq:
                case BinaryOperator.Opcode.Iff:
                    // we don't distinguish between equality and equivalence at this point
                    return VCExpressionGenerator.EqOp;
                case BinaryOperator.Opcode.Neq:
                    return VCExpressionGenerator.NeqOp;
                case BinaryOperator.Opcode.Lt:
                    return VCExpressionGenerator.LtOp;
                case BinaryOperator.Opcode.Le:
                    return VCExpressionGenerator.LeOp;
                case BinaryOperator.Opcode.Ge:
                    return VCExpressionGenerator.GeOp;
                case BinaryOperator.Opcode.Gt:
                    return VCExpressionGenerator.GtOp;
                case BinaryOperator.Opcode.Imp:
                    return VCExpressionGenerator.ImpliesOp;
                case BinaryOperator.Opcode.And:
                    return VCExpressionGenerator.AndOp;
                case BinaryOperator.Opcode.Or:
                    return VCExpressionGenerator.OrOp;
                case BinaryOperator.Opcode.Subtype:
                    return VCExpressionGenerator.SubtypeOp;
                default:
                    Contract.Assert(false);
                    throw new NotImplementedException();
            }

        }

        public override string ToString()
        {
            var ret = "";
            if (isFalse) return "false";
            var first = true;
            for(int i = 0; i < PostPreds.Count; i++) 
            {
                if(value[i].isFalse) continue;
                
                if(value[i].isTrue)
                    ret += string.Format("{0}{1}", first ? "" : " && ", PostPreds[i]);
                else
                    ret += string.Format("{0}({1} ==> {2})", first ? "" : " && ", value[i], PostPreds[i]);

                first = false;
            }
            return ret;
        }


        #region ISummaryElement Members

        public ISummaryElement GetFlaseSummary(Program program, Implementation impl)
        {
            return new PredicateAbs();
        }

        public void Join(Dictionary<string, Model.Element> state)
        {
            // Evaluate each predicate on the state
            var prePredsVal = new bool[PrePreds.Count];
            var postPredsVal = new bool[PostPreds.Count];

            var indexSeq = new List<int>(); 
            for(int i = 0; i < PrePreds.Count; i++) indexSeq.Add(i);

            for (int i = 0; i < PrePreds.Count; i++)
            {
                var v = Eval(PrePreds[i], state);
                Debug.Assert(v is bool);
                prePredsVal[i] = (bool)v;
            }

            for (int i = 0; i < PostPreds.Count; i++)
            {
                var v = Eval(PostPreds[i], state);
                Debug.Assert(v is bool);
                postPredsVal[i] = (bool)v;
            }

            for (int i = 0; i < PostPreds.Count; i++)
            {
                // No hope for this post pred?
                if (!isFalse && value[i].isFalse) continue;

                var newDisj = new PredicateAbsDisjunct(true);
                if (!postPredsVal[i])
                {
                    newDisj = new PredicateAbsDisjunct(indexSeq.Where(j => !prePredsVal[j]), indexSeq.Where(j => prePredsVal[j]));
                }

                if (isFalse)
                    value[i] = newDisj;
                else 
                    value[i] = PredicateAbsDisjunct.And(value[i], newDisj);
            }

            isFalse = false;
        }

        public VCExpr GetSummaryExpr(Dictionary<string, VCExpr> incarnations, VCExpressionGenerator gen)
        {
            if (isFalse)
                return VCExpressionGenerator.False;

            var ret = VCExpressionGenerator.True;

            for(int i = 0; i < PostPreds.Count; i++)
            {
                ret = gen.AndSimp(ret, gen.ImpliesSimp(value[i].ToVcExpr(j => ToVcExpr(PrePreds[j], incarnations, gen), gen), ToVcExpr(PostPreds[i], incarnations, gen)));
            }

            return ret;
        }

        #endregion
    }

    class PredicateAbsDisjunct
    {
        List<PredicateAbsConjunct> conjuncts;
        public bool isTrue {get; private set;}
        public bool isFalse
        {
            get
            {
                if (isTrue) return false;
                return conjuncts.Count == 0;
            }
        }

        public PredicateAbsDisjunct(bool isTrue)
        {
            this.isTrue = isTrue;
            conjuncts = new List<PredicateAbsConjunct>();
        }

        private PredicateAbsDisjunct(List<PredicateAbsConjunct> conjuncts)
        {
            isTrue = false;
            this.conjuncts = conjuncts;
        }

        // Disjunct of singleton conjuncts
        public PredicateAbsDisjunct(IEnumerable<int> pos, IEnumerable<int> neg)
        {
            conjuncts = new List<PredicateAbsConjunct>();
            isTrue = false;
            pos.Iter(p => conjuncts.Add(PredicateAbsConjunct.Singleton(p, true)));
            neg.Iter(p => conjuncts.Add(PredicateAbsConjunct.Singleton(p, false)));
        }

        public static PredicateAbsDisjunct And(PredicateAbsDisjunct v1, PredicateAbsDisjunct v2)
        {
            if (v1.isTrue) return v2;
            if (v2.isTrue) return v1;

            var result = new List<PredicateAbsConjunct>();

            foreach (var c1 in v1.conjuncts)
            {
                foreach (var c2 in v2.conjuncts)
                {
                    var c = PredicateAbsConjunct.And(c1, c2);
                    if (c.isFalse) continue;
                    if (result.Any(cprime => c.implies(cprime))) continue;
                    var tmp = new List<PredicateAbsConjunct>();
                    tmp.Add(c);
                    result.Where(cprime => !cprime.implies(c)).Iter(cprime => tmp.Add(cprime));
                    result = tmp;
                }
            }

            return new PredicateAbsDisjunct(result);
        }

        public VCExpr ToVcExpr(Func<int, VCExpr> predToExpr, VCExpressionGenerator gen)
        {
            if (isTrue) return VCExpressionGenerator.True;
            var ret = VCExpressionGenerator.False;
            conjuncts.Iter(c => ret = gen.OrSimp(ret, c.ToVcExpr(predToExpr, gen)));
            return ret;
        }

        public override string ToString()
        {
            if(isTrue) 
                return "true";
            var ret = "";
            var first = true;
            foreach (var c in conjuncts)
            {
                if (c.isFalse) continue;
                ret += string.Format("{0}{1}", first ? "" : " || ", c);
                first = false;
            }
            return ret;
        }
    }

    class PredicateAbsConjunct
    {
        static int ConjunctBound = 3;

        public bool isFalse { get; private set; }
        HashSet<int> posPreds;
        HashSet<int> negPreds;

        public static void Initialize(int bound)
        {
            ConjunctBound = bound;
        }

        private void Normalize()
        {
            if (posPreds.Intersect(negPreds).Any() || negPreds.Intersect(posPreds).Any() || (posPreds.Count + negPreds.Count > ConjunctBound))
            {
                isFalse = true;
                posPreds = new HashSet<int>();
                negPreds = new HashSet<int>();
            }
        }

        public PredicateAbsConjunct(bool isFalse)
        {
            posPreds = new HashSet<int>();
            negPreds = new HashSet<int>();
            this.isFalse = isFalse;
        }

        public static PredicateAbsConjunct Singleton(int v, bool isPositive)
        {
            if (isPositive)
                return new PredicateAbsConjunct(new int[] { v }, new HashSet<int>());
            else
                return new PredicateAbsConjunct(new HashSet<int>(), new int[] { v });
        }

        public PredicateAbsConjunct(IEnumerable<int> pos, IEnumerable<int> neg)
        {
            isFalse = false;
            posPreds = new HashSet<int>(pos);
            negPreds = new HashSet<int>(neg);
            Normalize();
        }

        public static PredicateAbsConjunct And(PredicateAbsConjunct v1, PredicateAbsConjunct v2)
        {
            if (v1.isFalse || v2.isFalse) return new PredicateAbsConjunct(true);
            return new PredicateAbsConjunct(v1.posPreds.Union(v2.posPreds), v1.negPreds.Union(v2.negPreds));
        }

        public bool implies(PredicateAbsConjunct v)
        {
            if (isFalse) return true;
            if (v.isFalse) return false;
            return (posPreds.IsSupersetOf(v.posPreds) && negPreds.IsSupersetOf(v.negPreds));
        }

        public VCExpr ToVcExpr(Func<int, VCExpr> predToExpr, VCExpressionGenerator gen)
        {
            if (isFalse) return VCExpressionGenerator.False;
            var ret = VCExpressionGenerator.True;
            posPreds.Iter(p => ret = gen.AndSimp(ret, predToExpr(p)));
            negPreds.Iter(p => ret = gen.AndSimp(ret, gen.Not(predToExpr(p))));
            return ret;
        }

        public override string ToString()
        {
            if (isFalse)
                return "false";

            var ret = "";
            var first = true;
            foreach (var p in posPreds)
            {
                ret += string.Format("{0}{1}", first ? "" : " && ", PredicateAbs.PrePreds[p]);
                first = false;
            }
            foreach (var p in negPreds)
            {
                ret += string.Format("{0}!{1}", first ? "" : " && ", PredicateAbs.PrePreds[p]);
                first = false;
            }
            return ret;
        }
    }

    class FindSummaryPred : MutatingVCExprVisitor<bool>
    {
        public List<Tuple<string, VCExprNAry>> summaryPreds;

        public FindSummaryPred(VCExpressionGenerator gen)
            : base(gen)
        {
            summaryPreds = new List<Tuple<string, VCExprNAry>>();
        }

        protected override VCExpr/*!*/ UpdateModifiedNode(VCExprNAry/*!*/ originalNode,
                                              List<VCExpr/*!*/>/*!*/ newSubExprs,
            // has any of the subexpressions changed?
                                              bool changed,
                                              bool arg)
        {
            Contract.Ensures(Contract.Result<VCExpr>() != null);

            VCExpr ret;
            if (changed)
                ret = Gen.Function(originalNode.Op,
                                   newSubExprs, originalNode.TypeArguments);
            else
                ret = originalNode;

            VCExprNAry retnary = ret as VCExprNAry;
            if (retnary == null) return ret;
            var op = retnary.Op as VCExprBoogieFunctionOp;
            if (op == null)
                return ret;

            string calleeName = op.Func.Name;

            if (!calleeName.EndsWith(AbstractHoudini.summaryPredSuffix))
                return ret;

            summaryPreds.Add(Tuple.Create(calleeName.Substring(0, calleeName.Length - AbstractHoudini.summaryPredSuffix.Length), retnary));

            return ret;
        }

    }

    class AbstractHoudiniErrorReporter : ProverInterface.ErrorHandler
    {
        public Model model;

        public AbstractHoudiniErrorReporter()
        {
            model = null;
        }

        public override void OnModel(IList<string> labels, Model model)
        {
            Debug.Assert(model != null);
            //model.Write(Console.Out);
            this.model = model;
        }
    }


    public class InternalError : System.ApplicationException
    {
        public InternalError(string msg) : base(msg) { }

    };

    public class BoogieUtil
    {
        // Constructs a mapping from procedure names to the implementation
        public static Dictionary<string, Implementation> nameImplMapping(Program p)
        {
            var m = new Dictionary<string, Implementation>();
            foreach (Declaration d in p.TopLevelDeclarations)
            {
                if (d is Implementation)
                {
                    Implementation impl = d as Implementation;
                    m.Add(impl.Name, impl);
                }
            }

            return m;
        }

        // is "assert true"?
        public static bool isAssertTrue(Cmd cmd)
        {
            var acmd = cmd as AssertCmd;
            if (acmd == null) return false;
            var le = acmd.Expr as LiteralExpr;
            if (le == null) return false;
            if (le.IsTrue) return true;
            return false;
        }
    }

}
