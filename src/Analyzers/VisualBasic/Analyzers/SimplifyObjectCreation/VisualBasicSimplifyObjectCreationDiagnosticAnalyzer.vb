﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports Microsoft.CodeAnalysis.CodeStyle
Imports Microsoft.CodeAnalysis.Diagnostics
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Microsoft.CodeAnalysis.VisualBasic.SimplifyObjectCreation
    <DiagnosticAnalyzer(LanguageNames.VisualBasic)>
    Friend NotInheritable Class VisualBasicSimplifyObjectCreationDiagnosticAnalyzer
        Inherits AbstractBuiltInCodeStyleDiagnosticAnalyzer

        Public Sub New()
            MyBase.New(
                diagnosticId:=IDEDiagnosticIds.SimplifyObjectCreationDiagnosticId,
                enforceOnBuild:=EnforceOnBuildValues.SimplifyObjectCreation,
                [option]:=Nothing,
                title:=New LocalizableResourceString(NameOf(VisualBasicAnalyzersResources.Object_creation_can_be_simplified), VisualBasicAnalyzersResources.ResourceManager, GetType(VisualBasicAnalyzersResources)))
        End Sub

        Protected Overrides Sub InitializeWorker(context As AnalysisContext)
            context.RegisterSyntaxNodeAction(AddressOf AnalyzeVariableDeclarator, SyntaxKind.VariableDeclarator)
        End Sub

        Public Overrides Function GetAnalyzerCategory() As DiagnosticAnalyzerCategory
            Return DiagnosticAnalyzerCategory.SemanticSpanAnalysis
        End Function

        Private Sub AnalyzeVariableDeclarator(context As SyntaxNodeAnalysisContext)
            ' Finds and reports syntax on the form:
            ' Dim x As SomeType = New SomeType()
            ' which can be simplified to
            ' Dim x As New SomeType()

            Dim variableDeclarator = DirectCast(context.Node, VariableDeclaratorSyntax)
            Dim asClauseType = variableDeclarator.AsClause?.Type()
            If asClauseType Is Nothing Then
                Return
            End If

            Dim objectCreation = TryCast(variableDeclarator.Initializer?.Value, ObjectCreationExpressionSyntax)
            If objectCreation Is Nothing Then
                Return
            End If

            Dim symbolInfo = context.SemanticModel.GetTypeInfo(objectCreation)
            If symbolInfo.Type IsNot Nothing AndAlso symbolInfo.Type.Equals(symbolInfo.ConvertedType, SymbolEqualityComparer.Default) Then
                context.ReportDiagnostic(Diagnostic.Create(Descriptor, variableDeclarator.GetLocation()))
            End If
        End Sub
    End Class
End Namespace
