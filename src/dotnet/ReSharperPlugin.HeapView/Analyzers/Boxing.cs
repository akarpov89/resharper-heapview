using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using JetBrains.Diagnostics;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Conversions;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.CSharp.Util;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using JetBrains.Util;
using JetBrains.Util.DataStructures.Collections;
using ReSharperPlugin.HeapView.Highlightings;

namespace ReSharperPlugin.HeapView.Analyzers;

public abstract class Boxing
{
  private Boxing([NotNull] ITreeNode correspondingNode)
  {
    CorrespondingNode = correspondingNode;
  }

  [NotNull] public ITreeNode CorrespondingNode { get; }

  protected abstract bool IsPossible { get; }
  protected abstract string GetReason([NotNull] string indent = "");

  public abstract void Report([NotNull] IHighlightingConsumer consumer);

  [CanBeNull, Pure]
  public static Boxing TryFind(
    Conversion conversion, [NotNull] IExpressionType sourceExpressionType, [NotNull] IType targetType, [NotNull] ITreeNode correspondingNode)
  {
    switch (conversion.Kind)
    {
      case ConversionKind.Boxing:
      {
        return RefineBoxingConversionResult();
      }

      case ConversionKind.Unboxing:
      {
        return RefineUnboxingConversionResult();
      }

      case ConversionKind.ImplicitTuple:
      case ConversionKind.ImplicitTupleLiteral:
      case ConversionKind.ExplicitTuple:
      case ConversionKind.ExplicitTupleLiteral:
      {
        var components = new LocalList<Boxing>();

        foreach (var (nested, componentIndex) in conversion.GetTopLevelNestedConversionsWithTypeInfo().WithIndexes())
        {
          var componentNode = TryGetComponentNode(correspondingNode, componentIndex) ?? correspondingNode;

          var nestedBoxing = TryFind(nested.Conversion, nested.SourceType, nested.TargetType, componentNode);
          if (nestedBoxing != null)
          {
            components.Add(nestedBoxing);
          }
        }

        if (components.Count > 0)
        {
          return new InsideTupleConversion(components.ReadOnlyList(), correspondingNode);
        }

        break;
      }
    }

    return null;

    [CanBeNull]
    Boxing RefineBoxingConversionResult()
    {
      var sourceType = sourceExpressionType.ToIType();
      if (sourceType is IDeclaredType (ITypeParameter, _) sourceTypeParameterType)
      {
        Assertion.Assert(!sourceTypeParameterType.IsReferenceType());

        if (targetType.IsTypeParameterType())
        {
          if (sourceTypeParameterType.IsValueType())
            return new Ordinary(sourceExpressionType, targetType, correspondingNode, isPossible: true);

          return null; // very unlikely
        }

        if (!sourceTypeParameterType.IsValueType())
        {
          return new Ordinary(sourceExpressionType, targetType, correspondingNode, isPossible: true);
        }
      }

      return new Ordinary(sourceExpressionType, targetType, correspondingNode);
    }

    [CanBeNull]
    Boxing RefineUnboxingConversionResult()
    {
      var sourceType = sourceExpressionType.ToIType();

      // yep, some "unboxing" conversions do actually cause boxing at runtime
      if (sourceType != null && targetType.Classify == TypeClassification.REFERENCE_TYPE)
      {
        // value type to reference type
        if (sourceType.Classify == TypeClassification.VALUE_TYPE)
        {
          return new Ordinary(sourceExpressionType, targetType, correspondingNode);
        }

        // unconstrained generic to reference type
        return new Ordinary(sourceExpressionType, targetType, correspondingNode, isPossible: true);
      }

      return null;
    }
  }

  [CanBeNull]
  private static ITreeNode TryGetComponentNode([NotNull] ITreeNode nodeToHighlight, int componentIndex)
  {
    switch (nodeToHighlight)
    {
      // (object, int) t;
      // t = (1, 2);
      case ICSharpExpression sourceExpression
        when sourceExpression.GetOperandThroughParenthesis() is ITupleExpression tupleExpression:
      {
        foreach (var tupleComponent in tupleExpression.ComponentsEnumerable)
        {
          if (componentIndex == 0)
          {
            return tupleComponent.Value;
          }

          componentIndex--;
        }

        break;
      }

      // (object a, int b) = intIntTuple;
      case ICSharpExpression sourceExpression
        when AssignmentExpressionNavigator.GetBySource(sourceExpression.GetContainingParenthesizedExpression())
          is { AssignmentType: AssignmentType.EQ, Dest: ITupleExpression tupleExpression }:
      {
        foreach (var tupleComponent in tupleExpression.ComponentsEnumerable)
        {
          if (componentIndex == 0)
          {
            if (tupleComponent is { NameIdentifier: null, Value: IDeclarationExpression { TypeUsage: { } typeUsage } })
            {
              return typeUsage;
            }

            return null;
          }

          componentIndex--;
        }

        break;
      }

      // var t = ((object, int)) intIntTuple;
      case ITupleTypeUsage tupleTypeUsage:
      {
        foreach (var tupleTypeComponent in tupleTypeUsage.ComponentsEnumerable)
        {
          if (componentIndex == 0)
          {
            return tupleTypeComponent?.TypeUsage;
          }

          componentIndex--;
        }

        break;
      }
    }

    return null;
  }

  private sealed class Ordinary : Boxing
  {
    private readonly string myReason;

    public Ordinary(IExpressionType sourceExpressionType, IType targetType, ITreeNode correspondingNode, bool isPossible = false)
      : base(correspondingNode)
    {
      IsPossible = isPossible;

      var sourceTypeText = sourceExpressionType.GetPresentableName(CorrespondingNode.Language, TypePresentationStyle.Default).Text;
      var targetTypeText = targetType.GetPresentableName(CorrespondingNode.Language, TypePresentationStyle.Default).Text;
      myReason = $"conversion from '{sourceTypeText}' to '{targetTypeText}'";
    }

    protected override bool IsPossible { get; }

    protected override string GetReason(string indent = "") => indent + myReason;

    public override void Report(IHighlightingConsumer consumer)
    {
      var reason = GetReason();

      if (!IsPossible)
      {
        var description = reason + " requires boxing of the value type";
        consumer.AddHighlighting(
          new BoxingAllocationHighlighting(CorrespondingNode, description));
      }
      else
      {
        var description = reason + " possibly requires boxing of the value type";
        consumer.AddHighlighting(
          new PossibleBoxingAllocationHighlighting(CorrespondingNode, description));
      }
    }
  }

  private sealed class InsideTupleConversion : Boxing
  {
    private const string IndentLevel = "  ";

    public InsideTupleConversion([NotNull] IReadOnlyList<Boxing> componentBoxings, [NotNull] ITreeNode correspondingNode)
      : base(correspondingNode)
    {
      Assertion.Assert(componentBoxings.Count > 0);

      ComponentBoxings = componentBoxings;
    }

    [NotNull] public IReadOnlyList<Boxing> ComponentBoxings { get; }

    protected override bool IsPossible
    {
      get
      {
        foreach (var componentBoxing in ComponentBoxings)
        {
          if (!componentBoxing.IsPossible)
            return false;
        }

        return true;
      }
    }

    protected override string GetReason(string indent = "")
    {
      if (ComponentBoxings.Count == 1)
      {
        return ComponentBoxings[0].GetReason(indent);
      }

      using var builder = PooledStringBuilder.GetInstance();

      var innerIndent = indent + IndentLevel;

      foreach (var componentBoxing in ComponentBoxings)
      {
        builder.AppendLine(componentBoxing.GetReason(innerIndent));
      }

      return builder.ToString();
    }


    public override void Report(IHighlightingConsumer consumer)
    {
      var canUseIndividualReports = true;

      foreach (var componentBoxing in ComponentBoxings)
      {
        if (componentBoxing.CorrespondingNode == CorrespondingNode)
        {
          canUseIndividualReports = false;
          break;
        }
      }

      if (canUseIndividualReports)
      {
        foreach (var componentBoxing in ComponentBoxings)
        {
          componentBoxing.Report(consumer);
        }
      }
      else
      {
        var reason = GetReason();

        var isMultiline = reason.Contains("\n");



        if (!IsPossible)
        {
          var description = $"tuple component {reason} performs boxing of the value type: ";
          consumer.AddHighlighting(
            new BoxingAllocationHighlighting(CorrespondingNode, description));
        }
        else
        {
          var description = "tuple component conversion possible performs boxing of the value type: " + Environment.NewLine + reason;
          consumer.AddHighlighting(
            new PossibleBoxingAllocationHighlighting(CorrespondingNode, description));
        }
      }
    }
  }
}