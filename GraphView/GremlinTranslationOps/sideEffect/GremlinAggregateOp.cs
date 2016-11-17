﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GraphView.GremlinTranslationOps.sideEffect
{
    internal class GremlinAggregateOp: GremlinTranslationOperator
    {
        public string SideEffectKey;

        public GremlinAggregateOp(string sideEffectKey)
        {
            SideEffectKey = sideEffectKey;
        }

        public override GremlinToSqlContext GetContext()
        {
            GremlinToSqlContext inputContext = GetInputContext();
            return inputContext;
        }
    }
}