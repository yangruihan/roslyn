﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using Microsoft.CodeAnalysis.Internal.Log;
using FixAllScope = Microsoft.CodeAnalysis.CodeFixes.FixAllScope;

namespace Microsoft.CodeAnalysis.CodeFixesAndRefactorings
{
    internal interface IFixAllState
    {
        int CorrelationId { get; }
        IFixAllProvider FixAllProvider { get; }
        string? CodeActionEquivalenceKey { get; }
        object Provider { get; }
        FixAllScope Scope { get; }
        FixAllKind FixAllKind { get; }
        Document? Document { get; }
        Project Project { get; }
        Solution Solution { get; }

        IFixAllState With(
            Optional<(Document? document, Project project)> documentAndProject = default,
            Optional<FixAllScope> scope = default,
            Optional<string?> codeActionEquivalenceKey = default);
    }
}
