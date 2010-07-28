//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.IO;
using Microsoft.Boogie;
using Graphing;
using AI = Microsoft.AbstractInterpretationFramework;
using System.Diagnostics.Contracts;
using Microsoft.Basetypes;
using Microsoft.Boogie.VCExprAST;

namespace Microsoft.Boogie {
  public class CalleeCounterexampleInfo {
    public Counterexample counterexample;
    public List<object>/*!>!*/ args;

    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(counterexample != null);
      Contract.Invariant(cce.NonNullElements(args));
    }


    public CalleeCounterexampleInfo(Counterexample cex, List<object/*!>!*/> x) {
      Contract.Requires(cex != null);
      Contract.Requires(cce.NonNullElements(x));
      counterexample = cex;
      args = x;
    }
  }

  public abstract class Counterexample {
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(Trace != null);
      Contract.Invariant(cce.NonNullElements(relatedInformation));
      Contract.Invariant(cce.NonNullElements(calleeCounterexamples));
    }

    [Peer]
    public BlockSeq Trace;
    [Peer]
    public List<string>/*!>!*/ relatedInformation;

    public Dictionary<Absy, CalleeCounterexampleInfo> calleeCounterexamples;

    internal Counterexample(BlockSeq trace) {
      Contract.Requires(trace != null);
      this.Trace = trace;
      this.relatedInformation = new List<string>();
      this.calleeCounterexamples = new Dictionary<Absy, CalleeCounterexampleInfo>();
      // base();
    }

    public void AddCalleeCounterexample(Absy absy, CalleeCounterexampleInfo cex) {
      Contract.Requires(absy != null);
      Contract.Requires(cex != null);
      calleeCounterexamples[absy] = cex;
    }

    public void AddCalleeCounterexample(Dictionary<Absy, CalleeCounterexampleInfo> cs) {
      Contract.Requires(cce.NonNullElements(cs));
      foreach (Absy absy in cs.Keys) {
        AddCalleeCounterexample(absy, cs[absy]);
      }
    }

    public void Print(int spaces) {
      foreach (Block b in Trace) {
        Contract.Assert(b != null);
        if (b.tok == null) {
          Console.WriteLine("    <intermediate block>");
        } else {
          // for ErrorTrace == 1 restrict the output; 
          // do not print tokens with -17:-4 as their location because they have been 
          // introduced in the translation and do not give any useful feedback to the user
          if (!(CommandLineOptions.Clo.ErrorTrace == 1 && b.tok.line == -17 && b.tok.col == -4)) {
            for (int i = 0; i < spaces + 4; i++)
              Console.Write(" ");
            Console.WriteLine("{0}({1},{2}): {3}", b.tok.filename, b.tok.line, b.tok.col, b.Label);
            foreach (Cmd cmd in b.Cmds) {
              Contract.Assert(cmd != null);
              if (calleeCounterexamples.ContainsKey(cmd)) {
                AssumeCmd assumeCmd = cce.NonNull((AssumeCmd)cmd);
                NAryExpr naryExpr = (NAryExpr)cce.NonNull(assumeCmd.Expr);
                for (int i = 0; i < spaces + 4; i++)
                  Console.Write(" ");
                Console.WriteLine("Inlined call to procedure {0} begins", naryExpr.Fun.FunctionName);
                calleeCounterexamples[cmd].counterexample.Print(spaces + 4);
                for (int i = 0; i < spaces + 4; i++)
                  Console.Write(" ");
                Console.WriteLine("Inlined call to procedure {0} ends", naryExpr.Fun.FunctionName);
              }
            }
          }
        }
      }
    }

    public abstract int GetLocation();
  }

  public class CounterexampleComparer : IComparer<Counterexample> {
    public int Compare(Counterexample c1, Counterexample c2) {
      Contract.Requires(c1 != null);
      Contract.Requires(c2 != null);
      if (c1.GetLocation() == c2.GetLocation())
        return 0;
      if (c1.GetLocation() > c2.GetLocation())
        return 1;
      return -1;
    }
  }

  public class AssertCounterexample : Counterexample {
    [Peer]
    public AssertCmd FailingAssert;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(FailingAssert != null);
    }


    public AssertCounterexample(BlockSeq trace, AssertCmd failingAssert)
      : base(trace) {
      Contract.Requires(trace != null);
      Contract.Requires(failingAssert != null);
      this.FailingAssert = failingAssert;
      // base(trace);
    }

    public override int GetLocation() {
      return FailingAssert.tok.line * 1000 + FailingAssert.tok.col;
    }
  }

  public class CallCounterexample : Counterexample {
    public CallCmd FailingCall;
    public Requires FailingRequires;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(FailingCall != null);
      Contract.Invariant(FailingRequires != null);
    }


    public CallCounterexample(BlockSeq trace, CallCmd failingCall, Requires failingRequires)
      : base(trace) {
      Contract.Requires(!failingRequires.Free);
      Contract.Requires(trace != null);
      Contract.Requires(failingCall != null);
      Contract.Requires(failingRequires != null);
      this.FailingCall = failingCall;
      this.FailingRequires = failingRequires;
      // base(trace);
    }

    public override int GetLocation() {
      return FailingCall.tok.line * 1000 + FailingCall.tok.col;
    }
  }

  public class ReturnCounterexample : Counterexample {
    public TransferCmd FailingReturn;
    public Ensures FailingEnsures;
    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(FailingEnsures != null);
      Contract.Invariant(FailingReturn != null);
    }


    public ReturnCounterexample(BlockSeq trace, TransferCmd failingReturn, Ensures failingEnsures)
      : base(trace) {
      Contract.Requires(trace != null);
      Contract.Requires(failingReturn != null);
      Contract.Requires(failingEnsures != null);
      Contract.Requires(!failingEnsures.Free);
      this.FailingReturn = failingReturn;
      this.FailingEnsures = failingEnsures;
      // base(trace);
    }

    public override int GetLocation() {
      return FailingReturn.tok.line * 1000 + FailingReturn.tok.col;
    }
  }

  public class VerifierCallback {
    // reason == null means this is genuine counterexample returned by the prover
    // other reason means it's time out/memory out/crash
    public virtual void OnCounterexample(Counterexample ce, string/*?*/ reason) {
      Contract.Requires(ce != null);
    }

    // called in case resource is exceeded and we don't have counterexample
    public virtual void OnTimeout(string reason) {
      Contract.Requires(reason != null);
    }

    public virtual void OnOutOfMemory(string reason) {
      Contract.Requires(reason != null);
    }

    public virtual void OnProgress(string phase, int step, int totalSteps, double progressEstimate) {
    }

    public virtual void OnUnreachableCode(Implementation impl) {
      Contract.Requires(impl != null);
    }

    public virtual void OnWarning(string msg) {
      Contract.Requires(msg != null);
      switch (CommandLineOptions.Clo.PrintProverWarnings) {
        case CommandLineOptions.ProverWarnings.None:
          break;
        case CommandLineOptions.ProverWarnings.Stdout:
          Console.WriteLine("Prover warning: " + msg);
          break;
        case CommandLineOptions.ProverWarnings.Stderr:
          Console.Error.WriteLine("Prover warning: " + msg);
          break;
        default:
          Contract.Assume(false);
          throw new cce.UnreachableException();  // unexpected case
      }
    }
  }
}

////////////////////////////////////////////

namespace VC {
  using Bpl = Microsoft.Boogie;

  public class VCGenException : Exception {
    public VCGenException(string s)
      : base(s) {
    }
  }
  [ContractClassFor(typeof(ConditionGeneration))]
  public class ConditionGenerationContracts : ConditionGeneration {
    public override Outcome VerifyImplementation(Implementation impl, Program program, VerifierCallback callback) {
      Contract.Requires(impl != null);
      Contract.Requires(program != null);
      Contract.Requires(callback != null);
      Contract.EnsuresOnThrow<UnexpectedProverOutputException>(true);
      throw new NotImplementedException();
    }
    public ConditionGenerationContracts(Program p) : base(p) {
    }
  }

  [ContractClass(typeof(ConditionGenerationContracts))]
  public abstract class ConditionGeneration {
    protected internal object CheckerCommonState;

    public enum Outcome {
      Correct,
      Errors,
      TimedOut,
      OutOfMemory,
      Inconclusive
    }

    [ContractInvariantMethod]
    void ObjectInvariant() {
      Contract.Invariant(cce.NonNullElements(checkers));
      Contract.Invariant(cce.NonNullElements(incarnationOriginMap));
      Contract.Invariant(program != null);
    }

    protected readonly List<Checker>/*!>!*/ checkers = new List<Checker>();
    protected Implementation current_impl = null;

    // shared across each implementation; created anew for each implementation
    protected Hashtable /*Variable -> int*/ variable2SequenceNumber;
    public Dictionary<Incarnation, Absy>/*!>!*/ incarnationOriginMap = new Dictionary<Incarnation, Absy>();

    // used only by FindCheckerFor
    public Program program;
    protected string/*?*/ logFilePath;
    protected bool appendLogFile;

    public ConditionGeneration(Program p) {
      Contract.Requires(p != null);
      program = p;
    }

    /// <summary>
    /// Takes an implementation and constructs a verification condition and sends
    /// it to the theorem prover.
    /// Returns null if "impl" is correct.  Otherwise, returns a list of counterexamples,
    /// each counterexample consisting of an array of labels.
    /// </summary>
    /// <param name="impl"></param>
    public Outcome VerifyImplementation(Implementation impl, Program program, out List<Counterexample>/*?*/ errors) {
      Contract.Requires(impl != null);
      Contract.Requires(program != null);
      Contract.Requires(Contract.ForAll(Contract.ValueAtReturn(out errors),i=>i!=null));

      Contract.Ensures(Contract.Result<Outcome>() != Outcome.Errors || errors != null);
      Contract.EnsuresOnThrow<UnexpectedProverOutputException>(true);
      Helpers.ExtraTraceInformation("Starting implementation verification");

      CounterexampleCollector collector = new CounterexampleCollector();
      Outcome outcome = VerifyImplementation(impl, program, collector);
      if (outcome == Outcome.Errors) {
        errors = collector.examples;
      } else {
        errors = null;
      }

      Helpers.ExtraTraceInformation("Finished implementation verification");
      return outcome;
    }

    public Outcome StratifiedVerifyImplementation(Implementation impl, Program program, out List<Counterexample>/*?*/ errors) {
      Contract.Requires(impl != null);
      Contract.Requires(program != null);
      Contract.Requires(Contract.ForAll(Contract.ValueAtReturn(out errors),i=>i!=null));
      Contract.Ensures(Contract.Result<Outcome>() != Outcome.Errors || errors != null);
      Contract.EnsuresOnThrow<UnexpectedProverOutputException>(true);
      Helpers.ExtraTraceInformation("Starting implementation verification");

      CounterexampleCollector collector = new CounterexampleCollector();
      Outcome outcome = StratifiedVerifyImplementation(impl, program, collector);
      if (outcome == Outcome.Errors) {
        errors = collector.examples;
      } else {
        errors = null;
      }

      Helpers.ExtraTraceInformation("Finished implementation verification");
      return outcome;
    }

    public abstract Outcome VerifyImplementation(Implementation impl, Program program, VerifierCallback callback);

    public virtual Outcome StratifiedVerifyImplementation(Implementation impl, Program program, VerifierCallback callback) {
      
      Contract.Requires(impl != null);
      Contract.Requires(program != null);
      Contract.Requires(callback != null);
      Contract.EnsuresOnThrow<UnexpectedProverOutputException>(true);
      return VerifyImplementation(impl, program, callback);
    }

    /////////////////////////////////// Common Methods and Classes //////////////////////////////////////////

    #region Methods for injecting pre- and postconditions
    private static void
      ThreadInBlockExpr(Implementation impl,
                        Block targetBlock,
                        BlockExpr blockExpr,
                        bool replaceWithAssert,
                        TokenTextWriter debugWriter) {
      Contract.Requires(impl != null);
      Contract.Requires(blockExpr != null);
      Contract.Requires(targetBlock != null);
      // Go through blockExpr and for all blocks that have a "return e"
      // as their transfer command:
      //   Replace all "return e" with "assert/assume e"
      //   Change the transfer command to "goto targetBlock"
      // Then add all of the blocks in blockExpr to the implementation (at the end)
      foreach (Block b in blockExpr.Blocks) {
        Contract.Assert(b != null);
        ReturnExprCmd rec = b.TransferCmd as ReturnExprCmd;
        if (rec != null) { // otherwise it is a goto command
          if (replaceWithAssert) {
            Ensures ens = new Ensures(rec.tok, false, rec.Expr, null);
            Contract.Assert(ens != null);
            Cmd c = new AssertEnsuresCmd(ens);
            Contract.Assert(c != null);
            b.Cmds.Add(c);
          } else {
            b.Cmds.Add(new AssumeCmd(rec.tok, rec.Expr));
          }
          b.TransferCmd = new GotoCmd(Token.NoToken,
            new StringSeq(targetBlock.Label),
            new BlockSeq(targetBlock));
          targetBlock.Predecessors.Add(b);
        }
        impl.Blocks.Add(b);
      }
      if (debugWriter != null) {
        blockExpr.Emit(debugWriter, 1, false);
      }
      return;
    }

    private static void AddAsPrefix(Block b, CmdSeq cs) {
      Contract.Requires(b != null);
      Contract.Requires(cs != null);
      CmdSeq newCommands = new CmdSeq();
      newCommands.AddRange(cs);
      newCommands.AddRange(b.Cmds);
      b.Cmds = newCommands;
    }


    /// <summary>
    /// Modifies an implementation by prepending it with startCmds and then, as assume
    /// statements, all preconditions.  Insert new blocks as needed, and adjust impl.Blocks[0]
    /// accordingly to make it the new implementation entry block.
    /// </summary>
    /// <param name="impl"></param>
    /// <param name="startCmds"></param>
    protected static void InjectPreconditions(Implementation impl, [Captured] CmdSeq startCmds) {
      Contract.Requires(impl != null);
      Contract.Requires(startCmds != null);
      Contract.Requires(impl.Proc != null);

      TokenTextWriter debugWriter = null;
      if (CommandLineOptions.Clo.PrintWithUniqueASTIds) {
        debugWriter = new TokenTextWriter("<console>", Console.Out, false);
        debugWriter.WriteLine("Effective precondition:");
      }

      Substitution formalProcImplSubst = Substituter.SubstitutionFromHashtable(impl.GetImplFormalMap());
      string LabelPrefix = "PreconditionGeneratedEntry";
      int k = 0;

      Block origStartBlock = impl.Blocks[0];
      Block insertionPoint = new Block(
        new Token(-17, -4), LabelPrefix + k, startCmds,
        new GotoCmd(Token.NoToken, new StringSeq(origStartBlock.Label), new BlockSeq(origStartBlock)));
      k++;
      impl.Blocks[0] = insertionPoint;  // make insertionPoint the start block
      impl.Blocks.Add(origStartBlock);  // and put the previous start block at the end of the list

      // (free and checked) requires clauses
      foreach (Requires req in impl.Proc.Requires)
      // invariant:  insertionPoint.TransferCmd is "goto origStartBlock;", but origStartBlock.Predecessors has not yet been updated
      {
        Contract.Assert(req != null);
        Expr e = Substituter.Apply(formalProcImplSubst, req.Condition);
        BlockExpr be = e as BlockExpr;
        if (be == null) {
          // This is the normal case, where the precondition is an ordinary expression
          Cmd c = new AssumeCmd(req.tok, e);
          insertionPoint.Cmds.Add(c);
          if (debugWriter != null) {
            c.Emit(debugWriter, 1);
          }
        } else {
          // This is a BlockExpr, so append all of its blocks (changing return expressions
          // to assume statements), make the insertion-point block goto the head block of the
          // BlockExpr, and create a new empty block as the current insertion point.
          // Here goes:  First, create the new block, which will become the new insertion
          // point and which will serve as a target for the BlockExpr.  Move the goto's from
          // the current insertion point to this new block.
          Block nextIP = new Block(new Token(-17, -4), LabelPrefix + k, new CmdSeq(), insertionPoint.TransferCmd);
          k++;
          // Second, append the BlockExpr blocks to the implementation's blocks
          ThreadInBlockExpr(impl, nextIP, be, false, debugWriter);
          // Third, make the old insertion-point block goto the entry block of the BlockExpr
          Block beEntry = cce.NonNull(be.Blocks[0]);
          insertionPoint.TransferCmd = new GotoCmd(Token.NoToken, new StringSeq(beEntry.Label), new BlockSeq(beEntry));
          beEntry.Predecessors.Add(insertionPoint);
          // Fourth, update the insertion point
          insertionPoint = nextIP;
        }
      }
      origStartBlock.Predecessors.Add(insertionPoint);

      if (debugWriter != null) {
        debugWriter.WriteLine();
      }
    }
    /// <summary>
    /// Modifies an implementation by inserting all postconditions
    /// as assert statements at the end of the implementation
    /// Returns the possibly-new unified exit block of the implementation
    /// </summary>
    /// <param name="impl"></param>
    /// <param name="unifiedExitblock">The unified exit block that has
    /// already been constructed for the implementation (and so
    /// is already an element of impl.Blocks)
    /// </param>
    protected static Block InjectPostConditions(Implementation impl, Block unifiedExitBlock, Hashtable/*TransferCmd->ReturnCmd*/ gotoCmdOrigins) {
      Contract.Requires(impl != null);
      Contract.Requires(unifiedExitBlock != null);
      Contract.Requires(gotoCmdOrigins != null);
      Contract.Requires(impl.Proc != null);
      Contract.Requires(unifiedExitBlock.TransferCmd is ReturnCmd);
      Contract.Ensures(Contract.Result<Block>() != null);
      Contract.Ensures(Contract.Result<Block>().TransferCmd is ReturnCmd);

      TokenTextWriter debugWriter = null;
      if (CommandLineOptions.Clo.PrintWithUniqueASTIds) {
        debugWriter = new TokenTextWriter("<console>", Console.Out, false);
        debugWriter.WriteLine("Effective postcondition:");
      }

      Substitution formalProcImplSubst = Substituter.SubstitutionFromHashtable(impl.GetImplFormalMap());
      Block insertionPoint = unifiedExitBlock;
      string LabelPrefix = "ReallyLastGeneratedExit";
      int k = 0;

      // (free and checked) ensures clauses
      foreach (Ensures ens in impl.Proc.Ensures) {
        cce.LoopInvariant(insertionPoint.TransferCmd is ReturnCmd);

        Contract.Assert(ens != null);
        if (!ens.Free) { // skip free ensures clauses
          Expr e = Substituter.Apply(formalProcImplSubst, ens.Condition);
          BlockExpr be = ens.Condition as BlockExpr;
          if (be == null) {
            // This is the normal case, where the postcondition is an ordinary expression
            Ensures ensCopy = (Ensures)cce.NonNull(ens.Clone());
            ensCopy.Condition = e;
            AssertEnsuresCmd c = new AssertEnsuresCmd(ensCopy);
            c.ErrorDataEnhanced = ensCopy.ErrorDataEnhanced;
            insertionPoint.Cmds.Add(c);
            if (debugWriter != null) {
              c.Emit(debugWriter, 1);
            }
          } else {
            // This is a BlockExpr, so append all of its blocks (changing return expressions
            // to assert statements), insert a goto to its head block from the current insertion
            // point, and create a new empty block as the current insertion point.
            // Here goes:  First, create the new block, which will become the new insertion
            // point and which will serve as a target for the BlockExpr.  Steal the TransferCmd
            // from insertionPoint, since insertionPoint's TransferCmd will soon be replaced anyhow.
            Block nextIP = new Block(new Token(-17, -4), LabelPrefix + k, new CmdSeq(), insertionPoint.TransferCmd);
            k++;
            // Second, append the BlockExpr blocks to the implementation's blocks
            ThreadInBlockExpr(impl, nextIP, be, true, debugWriter);
            // Third, make the old insertion-point block goto the entry block of the BlockExpr
            Block beEntry = cce.NonNull(be.Blocks[0]);
            insertionPoint.TransferCmd = new GotoCmd(Token.NoToken, new StringSeq(beEntry.Label), new BlockSeq(beEntry));
            beEntry.Predecessors.Add(insertionPoint);
            // Fourth, update the insertion point
            insertionPoint = nextIP;
          }
        }
      }

      if (debugWriter != null) {
        debugWriter.WriteLine();
      }
      return insertionPoint;
    }


    /// <summary>
    /// Get the pre-condition of an implementation, including the where clauses from the in-parameters.
    /// </summary>
    /// <param name="impl"></param>
    protected static CmdSeq GetPre(Implementation impl) {
      Contract.Requires(impl != null);
      Contract.Requires(impl.Proc != null);
      Contract.Ensures(Contract.Result<CmdSeq>() != null);


      TokenTextWriter debugWriter = null;
      if (CommandLineOptions.Clo.PrintWithUniqueASTIds) {
        debugWriter = new TokenTextWriter("<console>", Console.Out, false);
        debugWriter.WriteLine("Effective precondition:");
      }

      Substitution formalProcImplSubst = Substituter.SubstitutionFromHashtable(impl.GetImplFormalMap());
      CmdSeq pre = new CmdSeq();

      // (free and checked) requires clauses
      foreach (Requires req in impl.Proc.Requires) {
        Contract.Assert(req != null);
        Expr e = Substituter.Apply(formalProcImplSubst, req.Condition);
        Contract.Assert(e != null);
        Cmd c = new AssumeCmd(req.tok, e);
        Contract.Assert(c != null);
        pre.Add(c);

        if (debugWriter != null) {
          c.Emit(debugWriter, 1);
        }
      }

      if (debugWriter != null) {
        debugWriter.WriteLine();
      }

      return pre;
    }

    /// <summary>
    /// Get the post-condition of an implementation.
    /// </summary>
    /// <param name="impl"></param>
    protected static CmdSeq GetPost(Implementation impl) {


      Contract.Requires(impl != null);
      Contract.Requires(impl.Proc != null);
      Contract.Ensures(Contract.Result<CmdSeq>() != null);
      if (CommandLineOptions.Clo.PrintWithUniqueASTIds) {
        Console.WriteLine("Effective postcondition:");
      }

      // Construct an Expr for the post-condition
      Substitution formalProcImplSubst = Substituter.SubstitutionFromHashtable(impl.GetImplFormalMap());
      CmdSeq post = new CmdSeq();
      foreach (Ensures ens in impl.Proc.Ensures) {
        Contract.Assert(ens != null);
        if (!ens.Free) {
          Expr e = Substituter.Apply(formalProcImplSubst, ens.Condition);
          Contract.Assert(e != null);
          Ensures ensCopy = cce.NonNull((Ensures)ens.Clone());
          ensCopy.Condition = e;
          Cmd c = new AssertEnsuresCmd(ensCopy);
          ((AssertEnsuresCmd)c).ErrorDataEnhanced = ensCopy.ErrorDataEnhanced;
          post.Add(c);

          if (CommandLineOptions.Clo.PrintWithUniqueASTIds) {
            c.Emit(new TokenTextWriter("<console>", Console.Out, false), 1);
          }
        }
      }

      if (CommandLineOptions.Clo.PrintWithUniqueASTIds) {
        Console.WriteLine();
      }

      return post;
    }

    /// <summary>
    /// Get the where clauses from the in- and out-parameters as
    /// a sequence of assume commands.
    /// As a side effect, this method adds these where clauses to the out parameters.
    /// </summary>
    /// <param name="impl"></param>
    protected static CmdSeq GetParamWhereClauses(Implementation impl) {
      Contract.Requires(impl != null);
      Contract.Requires(impl.Proc != null);
      Contract.Ensures(Contract.Result<CmdSeq>() != null);
      TokenTextWriter debugWriter = null;
      if (CommandLineOptions.Clo.PrintWithUniqueASTIds) {
        debugWriter = new TokenTextWriter("<console>", Console.Out, false);
        debugWriter.WriteLine("Effective precondition from where-clauses:");
      }

      Substitution formalProcImplSubst = Substituter.SubstitutionFromHashtable(impl.GetImplFormalMap());
      CmdSeq whereClauses = new CmdSeq();

      // where clauses of in-parameters
      foreach (Formal f in impl.Proc.InParams) {
        Contract.Assert(f != null);
        if (f.TypedIdent.WhereExpr != null) {
          Expr e = Substituter.Apply(formalProcImplSubst, f.TypedIdent.WhereExpr);
          Cmd c = new AssumeCmd(f.tok, e);
          whereClauses.Add(c);

          if (debugWriter != null) {
            c.Emit(debugWriter, 1);
          }
        }
      }

      // where clauses of out-parameters
      Contract.Assert(impl.OutParams.Length == impl.Proc.OutParams.Length);
      for (int i = 0; i < impl.OutParams.Length; i++) {
        Variable f = cce.NonNull(impl.Proc.OutParams[i]);
        if (f.TypedIdent.WhereExpr != null) {
          Expr e = Substituter.Apply(formalProcImplSubst, f.TypedIdent.WhereExpr);
          Cmd c = new AssumeCmd(f.tok, e);
          whereClauses.Add(c);

          Variable fi = cce.NonNull(impl.OutParams[i]);
          Contract.Assume(fi.TypedIdent.WhereExpr == null);
          fi.TypedIdent.WhereExpr = e;

          if (debugWriter != null) {
            c.Emit(debugWriter, 1);
          }
        }
      }

      if (debugWriter != null) {
        debugWriter.WriteLine();
      }

      return whereClauses;
    }

    #endregion


    protected Checker FindCheckerFor(Implementation impl, int timeout) {
      Contract.Ensures(Contract.Result<Checker>() != null);

      int i = 0;
      while (i < checkers.Count) {
        if (checkers[i].Closed) {
          checkers.RemoveAt(i);
          continue;
        } else {
          if (!checkers[i].IsBusy && checkers[i].WillingToHandle(impl, timeout))
            return checkers[i];
        }
        ++i;
      }
      string log = logFilePath;
      if (log != null && !log.Contains("@PROC@") && checkers.Count > 0)
        log = log + "." + checkers.Count;
      Checker ch = new Checker(this, program, log, appendLogFile, impl, timeout);
      Contract.Assert(ch != null);
      checkers.Add(ch);
      return ch;
    }


    public void Close() {
      foreach (Checker checker in checkers) {
        Contract.Assert(checker != null);
        checker.Close();
      }
    }


    protected class CounterexampleCollector : VerifierCallback {
      [ContractInvariantMethod]
      void ObjectInvariant() {
        Contract.Invariant(cce.NonNullElements(examples));
      }

      public readonly List<Counterexample>/*!>!*/ examples = new List<Counterexample>();
      public override void OnCounterexample(Counterexample ce, string/*?*/ reason) {
        Contract.Requires(ce != null);
        examples.Add(ce);
      }

      public override void OnUnreachableCode(Implementation impl) {
        Contract.Requires(impl != null);
        System.Console.WriteLine("found unreachable code:");
        EmitImpl(impl, false);
        // TODO report error about next to last in seq
      }
    }

    protected static void EmitImpl(Implementation impl, bool printDesugarings) {
      Contract.Requires(impl != null);
      int oldPrintUnstructured = CommandLineOptions.Clo.PrintUnstructured;
      CommandLineOptions.Clo.PrintUnstructured = 2;  // print only the unstructured program
      bool oldPrintDesugaringSetting = CommandLineOptions.Clo.PrintDesugarings;
      CommandLineOptions.Clo.PrintDesugarings = printDesugarings;
      impl.Emit(new TokenTextWriter("<console>", Console.Out, false), 0);
      CommandLineOptions.Clo.PrintDesugarings = oldPrintDesugaringSetting;
      CommandLineOptions.Clo.PrintUnstructured = oldPrintUnstructured;
    }


    protected Block GenerateUnifiedExit(Implementation impl, Hashtable gotoCmdOrigins) {
      Contract.Requires(impl != null);
      Contract.Requires(gotoCmdOrigins != null);
      Contract.Ensures(Contract.Result<Block>() != null);

      Contract.Ensures(Contract.Result<Block>().TransferCmd is ReturnCmd);
      Block/*?*/ exitBlock = null;
      #region Create a unified exit block, if there's more than one
      {
        int returnBlocks = 0;
        foreach (Block b in impl.Blocks) {
          if (b.TransferCmd is ReturnCmd) {
            exitBlock = b;
            returnBlocks++;
          }
        }
        if (returnBlocks > 1) {
          string unifiedExitLabel = "GeneratedUnifiedExit";
          Block unifiedExit = new Block(new Token(-17, -4), unifiedExitLabel, new CmdSeq(), new ReturnCmd(Token.NoToken));
          Contract.Assert(unifiedExit != null);
          foreach (Block b in impl.Blocks) {
            if (b.TransferCmd is ReturnCmd) {
              StringSeq labels = new StringSeq();
              labels.Add(unifiedExitLabel);
              BlockSeq bs = new BlockSeq();
              bs.Add(unifiedExit);
              GotoCmd go = new GotoCmd(Token.NoToken, labels, bs);
              gotoCmdOrigins[go] = b.TransferCmd;
              b.TransferCmd = go;
              unifiedExit.Predecessors.Add(b);
            }
          }

          exitBlock = unifiedExit;
          impl.Blocks.Add(unifiedExit);
        }
        Contract.Assert(exitBlock != null);
      }
      return exitBlock;
      #endregion
    }

    /// <summary>
    /// Helperfunction to restore the predecessor relations after loop unrolling
    /// </summary> 
    protected void ComputePredecessors(List<Block>/*!>!*/ blocks) {
      Contract.Requires(cce.NonNullElements(blocks));
      #region Compute and store the Predecessor Relation on the blocks
      // This code just here to try things out.
      // Compute the predecessor relation for each block
      // Store it in the Predecessors field within each block
      foreach (Block b in blocks) {
        GotoCmd gtc = b.TransferCmd as GotoCmd;
        if (gtc != null) {
          Contract.Assume(gtc.labelTargets != null);
          foreach (Block dest in gtc.labelTargets) {
            Contract.Assert(dest != null);
            dest.Predecessors.Add(b);
          }
        }
      }
      #endregion Compute and store the Predecessor Relation on the blocks
    }

    protected static void ResetPredecessors(List<Block/*!>!*/> blocks) {
      Contract.Requires(blocks != null);
      foreach (Block b in blocks) {
        Contract.Assert(b != null);
        b.Predecessors = new BlockSeq();
      }
      foreach (Block b in blocks) {
        Contract.Assert(b != null);
        foreach (Block ch in Exits(b)) {
          Contract.Assert(ch != null);
          ch.Predecessors.Add(b);
        }
      }
    }

    protected static IEnumerable Exits(Block b) {
      Contract.Requires(b != null);
      GotoCmd g = b.TransferCmd as GotoCmd;
      if (g != null) {
        return cce.NonNull(g.labelTargets);
      }
      return new List<Block>();
    }

    protected Variable CreateIncarnation(Variable x, Absy a) {
      Contract.Requires(this.variable2SequenceNumber != null);
      Contract.Requires(this.current_impl != null);
      Contract.Requires(a is Block || a is AssignCmd || a is HavocCmd);

      Contract.Requires(x != null);
      Contract.Ensures(Contract.Result<Variable>() != null);

      int currentIncarnationNumber =
        variable2SequenceNumber.ContainsKey(x)
        ?
        (int)cce.NonNull(variable2SequenceNumber[x])
        :
        -1;
      Variable v = new Incarnation(x, currentIncarnationNumber + 1);
      variable2SequenceNumber[x] = currentIncarnationNumber + 1;
      Contract.Assert(current_impl != null);  // otherwise, the field current_impl wasn't set
      current_impl.LocVars.Add(v);
      incarnationOriginMap.Add((Incarnation)v, a);
      return v;
    }

    /// <summary>
    /// Compute the incarnation map at the beginning of block "b" from the incarnation blocks of the
    /// predecessors of "b".
    /// 
    /// The predecessor map b.map for block "b" is defined as follows:
    ///   b.map.Domain == Union{Block p in b.predecessors; p.map.Domain}
    ///   Forall{Variable v in b.map.Domain;
    ///              b.map[v] == (v in Intersection{Block p in b.predecessors; p.map}.Domain
    ///                           ?  b.predecessors[0].map[v]
    ///                           :  new Variable())}
    /// Every variable that b.map maps to a fresh variable requires a fixup in all predecessor blocks.
    /// </summary>
    /// <param name="b"></param>
    /// <param name="block2Incarnation">Gives incarnation maps for b's predecessors.</param>
    /// <returns></returns>
    protected Hashtable /*Variable -> Expr*/ ComputeIncarnationMap(Block b, Hashtable /*Variable -> Expr*/ block2Incarnation) {
      Contract.Requires(b != null);
      Contract.Requires(block2Incarnation != null);
      Contract.Ensures(Contract.Result<Hashtable>() != null);

      if (b.Predecessors.Length == 0) {
        return new Hashtable();
      }

      Hashtable /*Variable -> Expr*/ incarnationMap = null;
      Set /*Variable*/ fixUps = new Set /*Variable*/ ();
      foreach (Block pred in b.Predecessors) {
        Contract.Assert(pred != null);
        Contract.Assert(block2Incarnation.Contains(pred));  // otherwise, Passive Transformation found a block whose predecessors have not been processed yet
        Hashtable /*Variable -> Expr*/ predMap = (Hashtable /*Variable -> Expr*/)block2Incarnation[pred];
        Contract.Assert(predMap != null);
        if (incarnationMap == null) {
          incarnationMap = (Hashtable /*Variable -> Expr*/)predMap.Clone();
          continue;
        }

        ArrayList /*Variable*/ conflicts = new ArrayList /*Variable*/ ();
        foreach (Variable v in incarnationMap.Keys) {
          Contract.Assert(v != null);
          if (!predMap.Contains(v)) {
            // conflict!!
            conflicts.Add(v);
            fixUps.Add(v);
          }
        }
        // Now that we're done with enumeration, we'll do all the removes
        foreach (Variable v in conflicts) {
          Contract.Assert(v != null);
          incarnationMap.Remove(v);
        }
        foreach (Variable v in predMap.Keys) {
          Contract.Assert(v != null);
          if (!incarnationMap.Contains(v)) {
            // v was not in the domain of the predecessors seen so far, so it needs to be fixed up
            fixUps.Add(v);
          } else {
            // v in incarnationMap ==> all pred blocks (up to now) all agree on its incarnation
            if (predMap[v] != incarnationMap[v]) {
              // conflict!!
              incarnationMap.Remove(v);
              fixUps.Add(v);
            }
          }
        }
      }

      #region Second, for all variables in the fixups list, introduce a new incarnation and push it back into the preds.
      foreach (Variable v in fixUps) {
        Contract.Assert(v != null);
        if (!b.IsLive(v))
          continue;
        Variable v_prime = CreateIncarnation(v, b);
        IdentifierExpr ie = new IdentifierExpr(v_prime.tok, v_prime);
        Contract.Assert(incarnationMap != null);
        incarnationMap[v] = ie;
        foreach (Block pred in b.Predecessors) {
          Contract.Assert(pred != null);
          #region Create an assume command equating v_prime with its last incarnation in pred
          #region Create an identifier expression for the last incarnation in pred
          Hashtable /*Variable -> Expr*/ predMap = (Hashtable /*Variable -> Expr*/)cce.NonNull(block2Incarnation[pred]);
          Expr pred_incarnation_exp;
          Expr o = (Expr)predMap[v];
          if (o == null) {
            Variable predIncarnation = v;
            IdentifierExpr ie2 = new IdentifierExpr(predIncarnation.tok, predIncarnation);
            pred_incarnation_exp = ie2;
          } else {
            pred_incarnation_exp = o;
          }
          #endregion
          #region Create an identifier expression for the new incarnation
          IdentifierExpr v_prime_exp = new IdentifierExpr(v_prime.tok, v_prime);
          #endregion
          #region Create the assume command itself
          Expr e = Expr.Binary(Token.NoToken,
            BinaryOperator.Opcode.Eq,
            v_prime_exp,
            pred_incarnation_exp
            );
          AssumeCmd ac = new AssumeCmd(v.tok, e);
          pred.Cmds.Add(ac);
          #endregion
          #endregion
        }
      }
      #endregion

      Contract.Assert(incarnationMap != null);
      return incarnationMap;
    }

    Hashtable preHavocIncarnationMap = null;     // null = the previous command was not an HashCmd. Otherwise, a *copy* of the map before the havoc statement

    protected void TurnIntoPassiveBlock(Block b, Hashtable /*Variable -> Expr*/ incarnationMap, Substitution oldFrameSubst) {
      Contract.Requires(b != null);
      Contract.Requires(incarnationMap != null);
      Contract.Requires(oldFrameSubst != null);
      #region Walk forward over the commands in this block and convert them to passive commands

      CmdSeq passiveCmds = new CmdSeq();
      foreach (Cmd c in b.Cmds) {
        Contract.Assert(b != null); // walk forward over the commands because the map gets modified in a forward direction      
        TurnIntoPassiveCmd(c, incarnationMap, oldFrameSubst, passiveCmds);
      }
      b.Cmds = passiveCmds;

      #endregion
    }

    protected Hashtable /*Variable -> Expr*/ Convert2PassiveCmd(Implementation impl) {
      Contract.Requires(impl != null);
      #region Convert to Passive Commands

      #region Topological sort -- need to process in a linearization of the partial order
      Graph<Block> dag = new Graph<Block>();
      dag.AddSource(cce.NonNull(impl.Blocks[0])); // there is always at least one node in the graph
      foreach (Block b in impl.Blocks) {
        GotoCmd gtc = b.TransferCmd as GotoCmd;
        if (gtc != null) {
          Contract.Assume(gtc.labelTargets != null);
          foreach (Block dest in gtc.labelTargets) {
            Contract.Assert(dest != null);
            dag.AddEdge(b, dest);
          }
        }
      }
      IEnumerable sortedNodes = dag.TopologicalSort();
      Contract.Assert(sortedNodes != null);
      // assume sortedNodes != null;
      #endregion

      // Create substitution for old expressions
      Hashtable/*Variable!->Expr!*/ oldFrameMap = new Hashtable();
      Contract.Assume(impl.Proc != null);
      foreach (IdentifierExpr ie in impl.Proc.Modifies) {
        Contract.Assert(ie != null);
        if (!oldFrameMap.Contains(cce.NonNull(ie.Decl)))
          oldFrameMap.Add(ie.Decl, ie);
      }
      Substitution oldFrameSubst = Substituter.SubstitutionFromHashtable(oldFrameMap);

      // Now we can process the nodes in an order so that we're guaranteed to have
      // processed all of a node's predecessors before we process the node.
      Hashtable /*Block -> IncarnationMap*/ block2Incarnation = new Hashtable/*Block -> IncarnationMap*/();
      Block exitBlock = null;
      foreach (Block b in sortedNodes) {
        Contract.Assert(b != null);
        Contract.Assert(!block2Incarnation.Contains(b));
        Hashtable /*Variable -> Expr*/ incarnationMap = ComputeIncarnationMap(b, block2Incarnation);

        #region Each block's map needs to be available to successor blocks
        block2Incarnation.Add(b, incarnationMap);
        #endregion Each block's map needs to be available to successor blocks

        TurnIntoPassiveBlock(b, incarnationMap, oldFrameSubst);
        exitBlock = b;
      }

      // Verify that exitBlock is indeed the unique exit block
      Contract.Assert(exitBlock != null);
      Contract.Assert(exitBlock.TransferCmd is ReturnCmd);

      // We no longer need the where clauses on the out parameters, so we remove them to restore the situation from before VC generation
      foreach (Formal f in impl.OutParams) {
        Contract.Assert(f != null);
        f.TypedIdent.WhereExpr = null;
      }
      #endregion Convert to Passive Commands

      #region Debug Tracing
      if (CommandLineOptions.Clo.TraceVerify) {
        Console.WriteLine("after conversion to passive commands");
        EmitImpl(impl, true);
      }
      #endregion

      return (Hashtable)block2Incarnation[exitBlock];
    }

    /// <summary> 
    /// Turn a command into a passive command, and it remembers the previous step, to see if it is a havoc or not. In the case, it remebers the incarnation map BEFORE the havoc
    /// </summary> 
    protected void TurnIntoPassiveCmd(Cmd c, Hashtable /*Variable -> Expr*/ incarnationMap, Substitution oldFrameSubst, CmdSeq passiveCmds) {
      Contract.Requires(c != null);
      Contract.Requires(incarnationMap != null);
      Contract.Requires(oldFrameSubst != null);
      Contract.Requires(passiveCmds != null);
      Substitution incarnationSubst = Substituter.SubstitutionFromHashtable(incarnationMap);
      #region assert/assume P |--> assert/assume P[x := in(x)], out := in
      if (c is PredicateCmd) {
        Contract.Assert(c is AssertCmd || c is AssumeCmd);  // otherwise, unexpected PredicateCmd type

        PredicateCmd pc = (PredicateCmd)c.Clone();
        Contract.Assert(pc != null);

        Expr copy = Substituter.ApplyReplacingOldExprs(incarnationSubst, oldFrameSubst, pc.Expr);
        Contract.Assert(copy != null);
        if (pc is AssertCmd) {
          ((AssertCmd)pc).OrigExpr = pc.Expr;
          Contract.Assert(((AssertCmd)pc).IncarnationMap == null);
          ((AssertCmd)pc).IncarnationMap = (Hashtable /*Variable -> Expr*/)cce.NonNull(incarnationMap.Clone());
        }
        pc.Expr = copy;
        passiveCmds.Add(pc);
      }
      #endregion
      #region x1 := E1, x2 := E2, ... |--> assume x1' = E1[in] & x2' = E2[in], out := in( x |-> x' ) [except as noted below]
 else if (c is AssignCmd) {
        AssignCmd assign = ((AssignCmd)c).AsSimpleAssignCmd; // first remove map assignments
        Contract.Assert(assign != null);
        #region Substitute all variables in E with the current map
        List<Expr> copies = new List<Expr>();
        foreach (Expr e in assign.Rhss) {
          Contract.Assert(e != null);
          copies.Add(Substituter.ApplyReplacingOldExprs(incarnationSubst,
                                                        oldFrameSubst,
                                                        e));
        }
        #endregion

        List<Expr/*!>!*/> assumptions = new List<Expr>();
        // it might be too slow to create a new dictionary each time ...
        IDictionary<Variable, Expr> newIncarnationMappings =
          new Dictionary<Variable, Expr>();

        for (int i = 0; i < assign.Lhss.Count; ++i) {
          IdentifierExpr lhsIdExpr =
            cce.NonNull((SimpleAssignLhs)assign.Lhss[i]).AssignedVariable;
          Variable lhs = cce.NonNull(lhsIdExpr.Decl);
          Contract.Assert(lhs != null);
          Expr rhs = assign.Rhss[i];
          Contract.Assert(rhs != null);

          // don't create incarnations for assignments of literals or single variables.
          if (rhs is LiteralExpr) {
            incarnationMap[lhs] = rhs;
          } else if (rhs is IdentifierExpr) {
            IdentifierExpr ie = (IdentifierExpr)rhs;
            if (incarnationMap.ContainsKey(cce.NonNull(ie.Decl)))
              newIncarnationMappings[lhs] = cce.NonNull((Expr)incarnationMap[ie.Decl]);
            else
              newIncarnationMappings[lhs] = ie;
          } else {
            IdentifierExpr x_prime_exp = null;
            #region Make a new incarnation, x', for variable x, but only if x is *not* already an incarnation
            if (lhs is Incarnation) {
              // incarnations are already written only once, no need to make an incarnation of an incarnation
              x_prime_exp = lhsIdExpr;
            } else {
              Variable v = CreateIncarnation(lhs, c);
              x_prime_exp = new IdentifierExpr(lhsIdExpr.tok, v);
              newIncarnationMappings[lhs] = x_prime_exp;
            }
            #endregion
            #region Create an assume command with the new variable
            assumptions.Add(Expr.Eq(x_prime_exp, copies[i]));
            #endregion
          }
        }

        foreach (KeyValuePair<Variable, Expr> pair in newIncarnationMappings) {
          Contract.Assert(pair.Key != null && pair.Value != null);
          incarnationMap[pair.Key] = pair.Value;
        }

        if (assumptions.Count > 0) {
          Expr assumption = assumptions[0];

          for (int i = 1; i < assumptions.Count; ++i) {
            Contract.Assert(assumption != null);
            assumption = Expr.And(assumption, assumptions[i]);
          }
          passiveCmds.Add(new AssumeCmd(c.tok, assumption));
        }
      }
      #endregion
      #region havoc w |--> assume whereClauses, out := in( w |-> w' )
 else if (c is HavocCmd) {
        if (this.preHavocIncarnationMap == null)      // Save a copy of the incarnation map (at the top of a sequence of havoc statements)
          this.preHavocIncarnationMap = (Hashtable)incarnationMap.Clone();

        HavocCmd hc = (HavocCmd)c;
        Contract.Assert(c != null);
        IdentifierExprSeq havocVars = hc.Vars;
        // First, compute the new incarnations
        foreach (IdentifierExpr ie in havocVars) {
          Contract.Assert(ie != null);
          if (!(ie.Decl is Incarnation)) {
            Variable x = cce.NonNull(ie.Decl);
            Variable x_prime = CreateIncarnation(x, c);
            incarnationMap[x] = new IdentifierExpr(x_prime.tok, x_prime);
          }
        }
        // Then, perform the assume of the where clauses, using the updated incarnations
        Substitution updatedIncarnationSubst = Substituter.SubstitutionFromHashtable(incarnationMap);
        foreach (IdentifierExpr ie in havocVars) {
          Contract.Assert(ie != null);
          if (!(ie.Decl is Incarnation)) {
            Variable x = cce.NonNull(ie.Decl);
            Bpl.Expr w = x.TypedIdent.WhereExpr;
            if (w != null) {
              Expr copy = Substituter.ApplyReplacingOldExprs(updatedIncarnationSubst, oldFrameSubst, w);
              passiveCmds.Add(new AssumeCmd(c.tok, copy));
            }
          }
        }
      }
      #endregion
 else if (c is CommentCmd) {
        // comments are just for debugging and don't affect verification
      } else if (c is SugaredCmd) {
        SugaredCmd sug = (SugaredCmd)c;
        Contract.Assert(sug != null);
        Cmd cmd = sug.Desugaring;
        Contract.Assert(cmd != null);
        TurnIntoPassiveCmd(cmd, incarnationMap, oldFrameSubst, passiveCmds);
      } else if (c is StateCmd) {
        this.preHavocIncarnationMap = null;       // we do not need to remeber the previous incarnations
        StateCmd st = (StateCmd)c;
        Contract.Assert(st != null);
        // account for any where clauses among the local variables
        foreach (Variable v in st.Locals) {
          Contract.Assert(v != null);
          Expr w = v.TypedIdent.WhereExpr;
          if (w != null) {
            passiveCmds.Add(new AssumeCmd(v.tok, w));
          }
        }
        // do the sub-commands
        foreach (Cmd s in st.Cmds) {
          Contract.Assert(s != null);
          TurnIntoPassiveCmd(s, incarnationMap, oldFrameSubst, passiveCmds);
        }
        // remove the local variables from the incarnation map
        foreach (Variable v in st.Locals) {
          Contract.Assert(v != null);
          incarnationMap.Remove(v);
        }
      }
      #region There shouldn't be any other types of commands at this point
 else {
        Debug.Fail("Internal Error: Passive transformation handed a command that is not one of assert,assume,havoc,assign.");
      }
      #endregion


      #region We rember if we have put an havoc statement into a passive form

      if (!(c is HavocCmd))
        this.preHavocIncarnationMap = null;
      // else: it has already been set by the case for the HavocCmd    
      #endregion
    }

    /// <summary>
    /// Creates a new block to add to impl.Blocks, where impl is the implementation that contains
    /// succ.  Caller must do the add to impl.Blocks.
    /// </summary>
    protected Block CreateBlockBetween(int predIndex, Block succ) {
      Contract.Requires(0 <= predIndex && predIndex < succ.Predecessors.Length);


      Contract.Requires(succ != null);
      Contract.Ensures(Contract.Result<Block>() != null);

      Block pred = cce.NonNull(succ.Predecessors[predIndex]);

      string newBlockLabel = pred.Label + "_@2_" + succ.Label;

      // successor of newBlock list
      StringSeq ls = new StringSeq();
      ls.Add(succ.Label);
      BlockSeq bs = new BlockSeq();
      bs.Add(succ);

      Block newBlock = new Block(
          new Token(-17, -4),
          newBlockLabel,
          new CmdSeq(),
          new GotoCmd(Token.NoToken, ls, bs)
          );

      // predecessors of newBlock           
      BlockSeq ps = new BlockSeq();
      ps.Add(pred);
      newBlock.Predecessors = ps;

      // fix successors of pred
      #region Change the edge "pred->succ" to "pred->newBlock"
      GotoCmd gtc = (GotoCmd)cce.NonNull(pred.TransferCmd);
      Contract.Assume(gtc.labelTargets != null);
      Contract.Assume(gtc.labelNames != null);
      for (int i = 0, n = gtc.labelTargets.Length; i < n; i++) {
        if (gtc.labelTargets[i] == succ) {
          gtc.labelTargets[i] = newBlock;
          gtc.labelNames[i] = newBlockLabel;
          break;
        }
      }
      #endregion Change the edge "pred->succ" to "pred->newBlock"

      // fix predecessors of succ
      succ.Predecessors[predIndex] = newBlock;

      return newBlock;
    }

    protected void AddBlocksBetween(Implementation impl) {
      Contract.Requires(impl != null);
      #region Introduce empty blocks between join points and their multi-successor predecessors
      List<Block> tweens = new List<Block>();
      foreach (Block b in impl.Blocks) {
        int nPreds = b.Predecessors.Length;
        if (nPreds > 1) {
          // b is a join point (i.e., it has more than one predecessor)
          for (int i = 0; i < nPreds; i++) {
            GotoCmd gotocmd = (GotoCmd)(cce.NonNull(b.Predecessors[i]).TransferCmd);
            if (gotocmd.labelNames != null && gotocmd.labelNames.Length > 1) {
              tweens.Add(CreateBlockBetween(i, b));
            }
          }
        }
      }
      impl.Blocks.AddRange(tweens);  // must wait until iteration is done before changing the list
      #endregion
    }

  }
}