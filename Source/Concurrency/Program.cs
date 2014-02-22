﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.Boogie
{
    public class Concurrency
    {
        public static void Transform(LinearTypeChecker linearTypeChecker, MoverTypeChecker moverTypeChecker)
        {
            // The order in which originalDecls are computed and then *.AddCheckers are called is 
            // apparently important.  The MyDuplicator code currently does not duplicate Attributes.
            // Consequently, all the yield attributes are eliminated by the AddCheckers code.

            List<Declaration> originalDecls = new List<Declaration>();
            Program program = linearTypeChecker.program;
            foreach (var decl in program.TopLevelDeclarations)
            {
                Procedure proc = decl as Procedure;
                if (proc != null && QKeyValue.FindBoolAttribute(proc.Attributes, "yields"))
                {
                    originalDecls.Add(proc);
                    continue;
                }
                Implementation impl = decl as Implementation;
                if (impl != null && QKeyValue.FindBoolAttribute(impl.Proc.Attributes, "yields"))
                {
                    originalDecls.Add(impl);
                }
            }

            List<Declaration> decls = new List<Declaration>();
            OwickiGries.AddCheckers(linearTypeChecker, moverTypeChecker, decls);
            if (!CommandLineOptions.Clo.TrustAtomicityTypes)
            {
                MoverCheck.AddCheckers(linearTypeChecker, moverTypeChecker, decls);
            }
            foreach (Declaration decl in decls)
            {
                Procedure proc = decl as Procedure;
                if (proc != null && QKeyValue.FindBoolAttribute(proc.Attributes, "yields"))
                {
                    proc.Modifies = new List<IdentifierExpr>();
                    linearTypeChecker.program.GlobalVariables().Iter(x => proc.Modifies.Add(Expr.Ident(x)));
                }
            }
            foreach (Declaration decl in decls)
            {
                decl.Attributes = OwickiGries.RemoveYieldsAttribute(decl.Attributes);
                decl.Attributes = OwickiGries.RemoveStableAttribute(decl.Attributes);
            }
            program.TopLevelDeclarations.RemoveAll(x => originalDecls.Contains(x));
            program.TopLevelDeclarations.AddRange(decls);
        }

    }
}
