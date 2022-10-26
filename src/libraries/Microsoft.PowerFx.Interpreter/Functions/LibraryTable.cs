﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.PowerFx.Core.IR;
using Microsoft.PowerFx.Interpreter;
using Microsoft.PowerFx.Types;

namespace Microsoft.PowerFx.Functions
{
    internal static partial class Library
    {
        public static async ValueTask<FormulaValue> LookUp(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, FormulaValue[] args)
        {
            // Streaming 
            var arg0 = (TableValue)args[0];
            var arg1 = (LambdaFormulaValue)args[1];
            var arg2 = (LambdaFormulaValue)(args.Length > 2 ? args[2] : null);

            var rows = await LazyFilterAsync(runner, context, arg0.Rows, arg1);
            var row = rows.FirstOrDefault();

            if (row != null)
            {
                if (args.Length == 2)
                {
                    return row.ToFormulaValue() ?? new BlankValue(irContext);
                }
                else
                {
                    var childContext = context.SymbolContext.WithScopeValues(row.Value);
                    return await arg2.EvalInRowScopeAsync(context.NewScope(childContext));
                }
            }

            return new BlankValue(irContext);
        }

        public static FormulaValue First(IRContext irContext, TableValue[] args)
        {
            var arg0 = args[0];

            if (arg0 is QueryableTableValue tableQueryable)
            {
                try
                {
                    return tableQueryable.FirstN(1).Rows.FirstOrDefault()?.ToFormulaValue() ?? new BlankValue(irContext);
                }
                catch (NotDelegableException)
                {
                }
            }

            return arg0.Rows.FirstOrDefault()?.ToFormulaValue() ?? new BlankValue(irContext);
        }

        public static FormulaValue Last(IRContext irContext, TableValue[] args)
        {
            return args[0].Rows.LastOrDefault()?.ToFormulaValue() ?? new BlankValue(irContext);
        }

        public static FormulaValue FirstN(IRContext irContext, FormulaValue[] args)
        {
            if (args[0] is BlankValue)
            {
                return new BlankValue(irContext);
            }

            if (args[0] is not TableValue)
            {
                return CommonErrors.RuntimeTypeMismatch(irContext);
            }

            var arg0 = (TableValue)args[0];
            var arg1 = (NumberValue)args[1];

            if (arg0 is QueryableTableValue queryableTable)
            {
                try
                {
                    return queryableTable.FirstN((int)arg1.Value);
                }
                catch (NotDelegableException)
                {
                }
            }

            var rows = arg0.Rows.Take((int)arg1.Value);
            return new InMemoryTableValue(irContext, rows);
        }

        public static FormulaValue LastN(IRContext irContext, FormulaValue[] args)
        {
            if (args[0] is BlankValue)
            {
                return new BlankValue(irContext);
            }

            if (args[0] is not TableValue)
            {
                return CommonErrors.RuntimeTypeMismatch(irContext);
            }

            var arg0 = (TableValue)args[0];
            var arg1 = (NumberValue)args[1];

            // $$$ How to do on a streaming service?            
            var allRows = arg0.Rows.ToArray();
            var len = allRows.Length;
            var take = (int)arg1.Value; // $$$ rounding?

            var rows = allRows.Skip(len - take).Take(take);

            return new InMemoryTableValue(irContext, rows);
        }

        // Create new table
        public static async ValueTask<FormulaValue> AddColumns(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, FormulaValue[] args)
        {
            var sourceArg = (TableValue)args[0];

            var newColumns = NamedLambda.Parse(args);

            var tableType = (TableType)irContext.ResultType;
            var recordIRContext = new IRContext(irContext.SourceContext, tableType.ToRecord());
            var rows = await LazyAddColumnsAsync(runner, context, sourceArg.Rows, recordIRContext, newColumns);

            return new InMemoryTableValue(irContext, rows);
        }

        private static async Task<IEnumerable<DValue<RecordValue>>> LazyAddColumnsAsync(EvalVisitor runner, EvalVisitorContext context, IEnumerable<DValue<RecordValue>> sources, IRContext recordIRContext, NamedLambda[] newColumns)
        {
            var list = new List<DValue<RecordValue>>();

            foreach (var row in sources)
            {
                if (row.IsValue)
                {
                    // $$$ this is super inefficient... maybe a custom derived RecordValue? 
                    var fields = new List<NamedValue>(row.Value.Fields);

                    var childContext = context.SymbolContext.WithScopeValues(row.Value);

                    foreach (var column in newColumns)
                    {
                        var value = await column.Lambda.EvalInRowScopeAsync(context.NewScope(childContext));
                        fields.Add(new NamedValue(column.Name, value));
                    }

                    list.Add(DValue<RecordValue>.Of(new InMemoryRecordValue(recordIRContext, fields.ToArray())));
                }
                else
                {
                    list.Add(row);
                }
            }

            return list;
        }

        // CountRows
        public static FormulaValue CountRows(IRContext irContext, FormulaValue[] args)
        {
            var arg0 = args[0];

            if (arg0 is BlankValue)
            {
                return new NumberValue(irContext, 0);
            }

            if (arg0 is TableValue table)
            {
                var error = table.Rows.Where(r => r.IsError).Select(r => r.Error).FirstOrDefault();
                if (error != null)
                {
                    return error;
                }

                var count = table.Count();
                return new NumberValue(irContext, count);
            }

            return CommonErrors.RuntimeTypeMismatch(irContext);
        }

        // Count
        public static FormulaValue Count(IRContext irContext, FormulaValue[] args)
        {
            var arg0 = args[0];
            var count = 0;

            if (arg0 is BlankValue)
            {
                return new NumberValue(irContext, 0);
            }

            if (arg0 is TableValue table)
            {
                foreach (var row in table.Rows)
                {
                    if (row.IsBlank)
                    {
                        continue;
                    }
                    else if (row.IsError)
                    {
                        return row.Error;
                    }

                    var field = row.Value.Fields.First().Value;

                    if (field is ErrorValue error)
                    {
                        return error;
                    }

                    if (field is NumberValue)
                    {
                        count++;
                    }
                }

                return new NumberValue(irContext, count);
            }

            return CommonErrors.RuntimeTypeMismatch(irContext);
        }

        // CountA
        public static FormulaValue CountA(IRContext irContext, FormulaValue[] args)
        {
            var arg0 = args[0];
            if (arg0 is BlankValue)
            {
                return new NumberValue(irContext, 0);
            }

            if (arg0 is TableValue table)
            {
                var count = 0;

                foreach (var row in table.Rows)
                {
                    if (row.IsBlank)
                    {
                        continue;
                    }
                    else if (row.IsError)
                    {
                        return row.Error;
                    }

                    var field = row.Value.Fields.First().Value;

                    if (field is ErrorValue error)
                    {
                        return error;
                    }

                    if (field is not BlankValue)
                    {
                        count++;
                    }
                }

                return new NumberValue(irContext, count);
            }

            return CommonErrors.RuntimeTypeMismatch(irContext);
        }

        public static async ValueTask<FormulaValue> CountIf(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, FormulaValue[] args)
        {
            if (args[0] is BlankValue)
            {
                return new NumberValue(irContext, 0);
            }

            // Streaming 
            var sources = (TableValue)args[0];
            var filter = (LambdaFormulaValue)args[1];

            var count = 0;

            foreach (var row in sources.Rows)
            {
                if (row.IsValue || row.IsError)
                {
                    var childContext = row.IsValue ?
                        context.SymbolContext.WithScopeValues(row.Value) :
                        context.SymbolContext.WithScopeValues(row.Error);
                    var result = await filter.EvalInRowScopeAsync(context.NewScope(childContext));

                    if (result is ErrorValue error)
                    {
                        return error;
                    }

                    var include = ((BooleanValue)result).Value;

                    if (include)
                    {
                        count++;
                    }
                }
            }

            return new NumberValue(irContext, count);
        }

        // Filter ([1,2,3,4,5], Value > 5)
        public static async ValueTask<FormulaValue> FilterTable(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, FormulaValue[] args)
        {
            // Streaming 
            var arg0 = (TableValue)args[0];
            var arg1 = (LambdaFormulaValue)args[1];

            if (args.Length > 2)
            {
                return new ErrorValue(irContext, new ExpressionError()
                {
                    Message = "Filter() only supports one predicate",
                    Span = irContext.SourceContext,
                    Kind = ErrorKind.Validation
                });
            }

            if (arg0 is QueryableTableValue tableQueryable)
            {
                try
                {
                    return tableQueryable.Filter(arg1, runner, context);
                }
                catch (NotDelegableException)
                {
                }
            }

            var rows = await LazyFilterAsync(runner, context, arg0.Rows, arg1);

            return new InMemoryTableValue(irContext, rows);
        }

        public static FormulaValue IndexTable(IRContext irContext, FormulaValue[] args)
        {
            var arg0 = (TableValue)args[0];
            var arg1 = (NumberValue)args[1];
            var rowIndex = (int)arg1.Value;

            return arg0.Index(rowIndex).ToFormulaValue();
        }

        public static FormulaValue Shuffle(IServiceProvider services, IRContext irContext, FormulaValue[] args)
        {
            var table = (TableValue)args[0];
            var records = table.Rows;

            var random = services.GetService<IRandomService>(_defaultRandService);

            var shuffledRecords = records.OrderBy(a => random.SafeNextDouble()).ToList();
            return new InMemoryTableValue(irContext, shuffledRecords);
        }

        private static async Task<(DValue<RecordValue> row, FormulaValue sortValue)> ApplySortLambda(EvalVisitor runner, EvalVisitorContext context, DValue<RecordValue> row, LambdaFormulaValue lambda)
        {
            if (!row.IsValue)
            {
                return (row, row.ToFormulaValue());
            }

            var childContext = context.SymbolContext.WithScopeValues(row.Value);
            var sortValue = await lambda.EvalInRowScopeAsync(context.NewScope(childContext));

            return (row, sortValue);
        }

        private static async Task<(DValue<RecordValue> row, FormulaValue sortValue)> ApplyDistinctLambda(EvalVisitor runner, EvalVisitorContext context, DValue<RecordValue> row, LambdaFormulaValue lambda)
        {
            if (!row.IsValue)
            {
                return (row, row.ToFormulaValue());
            }

            var childContext = context.SymbolContext.WithScopeValues(row.Value);
            var distinctValue = await lambda.EvalAsync(runner, context.NewScope(childContext));

            return (row, distinctValue);
        }

        public static async ValueTask<FormulaValue> DistinctTable(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, FormulaValue[] args)
        {
            var arg0 = (TableValue)args[0];
            var arg1 = (LambdaFormulaValue)args[1];

            var pairs = (await Task.WhenAll(arg0.Rows.Select(row => ApplyDistinctLambda(runner, context, row, arg1)))).ToList();

            var errors = new List<ErrorValue>();
            bool allNumbers = true, allStrings = true, allBooleans = true, allDatetimes = true, allDates = true;

            foreach (var (row, sortValue) in pairs)
            {
                allNumbers &= IsValueTypeErrorOrBlank<NumberValue>(sortValue);
                allStrings &= IsValueTypeErrorOrBlank<StringValue>(sortValue);
                allBooleans &= IsValueTypeErrorOrBlank<BooleanValue>(sortValue);
                allDatetimes &= IsValueTypeErrorOrBlank<DateTimeValue>(sortValue);
                allDates &= IsValueTypeErrorOrBlank<DateValue>(sortValue);

                if (sortValue is ErrorValue errorValue)
                {
                    errors.Add(errorValue);
                }
            }

            if (!(allNumbers || allStrings || allBooleans || allDatetimes || allDates))
            {
                errors.Add(CommonErrors.RuntimeTypeMismatch(irContext));
                return ErrorValue.Combine(irContext, errors);
            }

            if (errors.Count != 0)
            {
                return ErrorValue.Combine(irContext, errors);
            }

            if (allNumbers)
            {
                return DistinctValueType<NumberValue, double>(pairs, irContext);
            }
            else if (allStrings)
            {
                return DistinctValueType<StringValue, string>(pairs, irContext);
            }
            else if (allBooleans)
            {
                return DistinctValueType<BooleanValue, bool>(pairs, irContext);
            }
            else if (allDatetimes)
            {
                return DistinctValueType<DateTimeValue, DateTime>(pairs, irContext);
            }
            else
            {
                return DistinctValueType<DateValue, DateTime>(pairs, irContext);
            }
        }

        public static async ValueTask<FormulaValue> SortTable(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, FormulaValue[] args)
        {
            var arg0 = (TableValue)args[0];
            var arg1 = (LambdaFormulaValue)args[1];
            var arg2 = (StringValue)args[2];

            var isDescending = arg2.Value.ToLower() == "descending";

            if (arg0 is QueryableTableValue queryableTable)
            {
                try
                {
                    return queryableTable.Sort(arg1, isDescending, runner, context);
                }
                catch (NotDelegableException)
                {
                }
            }

            var pairs = (await Task.WhenAll(arg0.Rows.Select(row => ApplySortLambda(runner, context, row, arg1)))).ToList();

            bool allNumbers = true, allStrings = true, allBooleans = true, allDatetimes = true, allDates = true, allOptionSets = true;

            foreach (var (row, sortValue) in pairs)
            {
                allNumbers &= IsValueTypeErrorOrBlank<NumberValue>(sortValue);
                allStrings &= IsValueTypeErrorOrBlank<StringValue>(sortValue);
                allBooleans &= IsValueTypeErrorOrBlank<BooleanValue>(sortValue);
                allDatetimes &= IsValueTypeErrorOrBlank<DateTimeValue>(sortValue);
                allDates &= IsValueTypeErrorOrBlank<DateValue>(sortValue);
                allOptionSets &= IsValueTypeErrorOrBlank<OptionSetValue>(sortValue);

                if (sortValue is ErrorValue errorValue)
                {
                    return errorValue;
                }
            }

            if (!(allNumbers || allStrings || allBooleans || allDatetimes || allDates || allOptionSets))
            {
                return CommonErrors.RuntimeTypeMismatch(irContext);
            }

            var compareToResultModifier = 1;
            if (isDescending)
            {
                compareToResultModifier = -1;
            }

            if (allNumbers)
            {
                return SortValueType<NumberValue, double>(pairs, irContext, compareToResultModifier);
            }
            else if (allStrings)
            {
                return SortValueType<StringValue, string>(pairs, irContext, compareToResultModifier);
            }
            else if (allBooleans)
            {
                return SortValueType<BooleanValue, bool>(pairs, irContext, compareToResultModifier);
            }
            else if (allDatetimes)
            {
                return SortValueType<DateTimeValue, DateTime>(pairs, irContext, compareToResultModifier);
            }
            else if (allDates)
            {
                return SortValueType<DateValue, DateTime>(pairs, irContext, compareToResultModifier);
            }
            else if (allOptionSets)
            {
                return SortOptionSet(pairs, irContext, compareToResultModifier);
            }
            else
            {
                return CommonErrors.RuntimeTypeMismatch(irContext);
            }
        }

        private static bool IsValueTypeErrorOrBlank<T>(FormulaValue val)
            where T : FormulaValue
        {
            return val is T || val is BlankValue || val is ErrorValue;
        }

        private static FormulaValue DistinctValueType<TPFxPrimitive, TDotNetPrimitive>(List<(DValue<RecordValue> row, FormulaValue sortValue)> pairs, IRContext irContext)
            where TPFxPrimitive : PrimitiveValue<TDotNetPrimitive>
            where TDotNetPrimitive : IComparable<TDotNetPrimitive>
        {
            var values = new Dictionary<TDotNetPrimitive, FormulaValue>();
            foreach (var (row, sortValue) in pairs)
            {
                var key = (TDotNetPrimitive)sortValue.ToObject();
                
                if (!values.ContainsKey(key))
                {
                    values.Add(key, RecordValue.NewRecordFromFields(new NamedValue(new KeyValuePair<string, FormulaValue>("Result", sortValue))));
                }
            }

            PrimitiveValueConversions.TryGetFormulaType(typeof(TDotNetPrimitive), out var fType);
            var test = RecordType.Empty().Add("Result", fType);

            return FormulaValue.NewTable(test, values.Values.Cast<RecordValue>());
        }

        private static FormulaValue SortValueType<TPFxPrimitive, TDotNetPrimitive>(List<(DValue<RecordValue> row, FormulaValue sortValue)> pairs, IRContext irContext, int compareToResultModifier)
            where TPFxPrimitive : PrimitiveValue<TDotNetPrimitive>
            where TDotNetPrimitive : IComparable<TDotNetPrimitive>
        {
            pairs.Sort((a, b) =>
            {
                if (a.sortValue is BlankValue)
                {
                    return b.sortValue is BlankValue ? 0 : 1;
                }
                else if (b.sortValue is BlankValue)
                {
                    return -1;
                }

                var n1 = a.sortValue as TPFxPrimitive;
                var n2 = b.sortValue as TPFxPrimitive;
                return n1.Value.CompareTo(n2.Value) * compareToResultModifier;
            });

            return new InMemoryTableValue(irContext, pairs.Select(pair => pair.row));
        }

        private static FormulaValue SortOptionSet(List<(DValue<RecordValue> row, FormulaValue sortValue)> pairs, IRContext irContext, int compareToResultModifier)
        {
            pairs.Sort((a, b) =>
            {
                if (a.sortValue is BlankValue)
                {
                    return b.sortValue is BlankValue ? 0 : 1;
                }
                else if (b.sortValue is BlankValue)
                {
                    return -1;
                }

                var n1 = a.sortValue as OptionSetValue;
                var n2 = b.sortValue as OptionSetValue;
                return n1.Option.CompareTo(n2.Option) * compareToResultModifier;
            });

            return new InMemoryTableValue(irContext, pairs.Select(pair => pair.row));
        }

        private static async Task<DValue<RecordValue>> LazyFilterRowAsync(
           EvalVisitor runner,
           EvalVisitorContext context,
           DValue<RecordValue> row,
           LambdaFormulaValue filter)
        {
            SymbolContext childContext;

            // Issue #263 Filter should be able to handle empty rows
            if (row.IsValue)
            {
                childContext = context.SymbolContext.WithScopeValues(row.Value);
            }
            else if (row.IsBlank)
            {
                childContext = context.SymbolContext.WithScopeValues(RecordValue.Empty());
            }
            else
            {
                return null;
            }

            // Filter evals to a boolean 
            var result = await filter.EvalInRowScopeAsync(context.NewScope(childContext));
            var include = false;
            if (result is BooleanValue booleanValue)
            {
                include = booleanValue.Value;
            }
            else if (result is ErrorValue errorValue)
            {
                return DValue<RecordValue>.Of(errorValue);
            }

            if (include)
            {
                return row;
            }

            return null;
        }

        private static async Task<DValue<RecordValue>[]> LazyFilterAsync(
            EvalVisitor runner,
            EvalVisitorContext context,
            IEnumerable<DValue<RecordValue>> sources,
            LambdaFormulaValue filter,
            int topN = int.MaxValue)
        {
            var tasks = new List<Task<DValue<RecordValue>>>();

            // Filter needs to allow running in parallel. 
            foreach (var row in sources)
            {
                runner.CheckCancel();

                var task = LazyFilterRowAsync(runner, context, row, filter);
                tasks.Add(task);
            }

            // WhenAll will allow running tasks in parallel. 
            var results = await Task.WhenAll(tasks);

            // Remove all nulls. 
            var final = results.Where(x => x != null);

            return final.ToArray();
        }

        // AddColumns accepts pairs of args. 
        private class NamedLambda
        {
            public string Name;

            public LambdaFormulaValue Lambda;

            public static NamedLambda[] Parse(FormulaValue[] args)
            {
                var l = new List<NamedLambda>();

                for (var i = 1; i < args.Length; i += 2)
                {
                    var columnName = ((StringValue)args[i]).Value;
                    var arg1 = (LambdaFormulaValue)args[i + 1];
                    l.Add(new NamedLambda
                    {
                        Name = columnName,
                        Lambda = arg1
                    });
                }

                return l.ToArray();
            }
        }
    }
}
