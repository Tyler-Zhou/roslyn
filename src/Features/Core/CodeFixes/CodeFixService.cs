// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes.Suppression;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CodeFixes
{
    using DiagnosticId = String;
    using LanguageKind = String;

    [Export(typeof(ICodeFixService))]
    internal partial class CodeFixService : ICodeFixService
    {
        private readonly IDiagnosticAnalyzerService _diagnosticService;

        private readonly ImmutableDictionary<LanguageKind, Lazy<ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>>>> _workspaceFixersMap;
        private readonly ConditionalWeakTable<IReadOnlyList<AnalyzerReference>, ImmutableDictionary<DiagnosticId, List<CodeFixProvider>>> _projectFixersMap;

        // Shared by project fixers and workspace fixers.
        private ImmutableDictionary<CodeFixProvider, ImmutableArray<DiagnosticId>> _fixerToFixableIdsMap = ImmutableDictionary<CodeFixProvider, ImmutableArray<DiagnosticId>>.Empty;

        private readonly ImmutableDictionary<LanguageKind, Lazy<ImmutableDictionary<CodeFixProvider, int>>> _fixerPriorityMap;

        private readonly ConditionalWeakTable<AnalyzerReference, ProjectCodeFixProvider> _analyzerReferenceToFixersMap;
        private readonly ConditionalWeakTable<AnalyzerReference, ProjectCodeFixProvider>.CreateValueCallback _createProjectCodeFixProvider;

        private readonly ImmutableDictionary<LanguageKind, Lazy<ISuppressionFixProvider>> _suppressionProvidersMap;

        private ImmutableDictionary<CodeFixProvider, FixAllProviderInfo> _fixAllProviderMap;

        [ImportingConstructor]
        public CodeFixService(
            IDiagnosticAnalyzerService service,
            [ImportMany]IEnumerable<Lazy<CodeFixProvider, CodeChangeProviderMetadata>> fixers,
            [ImportMany]IEnumerable<Lazy<ISuppressionFixProvider, CodeChangeProviderMetadata>> suppressionProviders)
        {
            _diagnosticService = service;

            var fixersPerLanguageMap = fixers.ToPerLanguageMapWithMultipleLanguages();
            var suppressionProvidersPerLanguageMap = suppressionProviders.ToPerLanguageMapWithMultipleLanguages();

            _workspaceFixersMap = GetFixerPerLanguageMap(fixersPerLanguageMap);
            _suppressionProvidersMap = GetSupressionProvidersPerLanguageMap(suppressionProvidersPerLanguageMap);

            // REVIEW: currently, fixer's priority is statically defined by the fixer itself. might considering making it more dynamic or configurable.
            _fixerPriorityMap = GetFixerPriorityPerLanguageMap(fixersPerLanguageMap);

            // Per-project fixers
            _projectFixersMap = new ConditionalWeakTable<IReadOnlyList<AnalyzerReference>, ImmutableDictionary<string, List<CodeFixProvider>>>();
            _analyzerReferenceToFixersMap = new ConditionalWeakTable<AnalyzerReference, ProjectCodeFixProvider>();
            _createProjectCodeFixProvider = new ConditionalWeakTable<AnalyzerReference, ProjectCodeFixProvider>.CreateValueCallback(r => new ProjectCodeFixProvider(r));
            _fixAllProviderMap = ImmutableDictionary<CodeFixProvider, FixAllProviderInfo>.Empty;
        }

        public async Task<FirstDiagnosticResult> GetFirstDiagnosticWithFixAsync(Document document, TextSpan range, CancellationToken cancellationToken)
        {
            if (document == null || !document.IsOpen())
            {
                return default(FirstDiagnosticResult);
            }

            using (var diagnostics = SharedPools.Default<List<DiagnosticData>>().GetPooledObject())
            {
                var fullResult = await _diagnosticService.TryAppendDiagnosticsForSpanAsync(document, range, diagnostics.Object, cancellationToken).ConfigureAwait(false);
                foreach (var diagnostic in diagnostics.Object)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!range.IntersectsWith(diagnostic.TextSpan))
                    {
                        continue;
                    }

                    // REVIEW: 2 possible designs. 
                    // 1. find the first fix and then return right away. if the lightbulb is actually expanded, find all fixes for the line synchronously. or
                    // 2. kick off a task that finds all fixes for the given range here but return once we find the first one. 
                    // at the same time, let the task to run to finish. if the lightbulb is expanded, we just simply use the task to get all fixes.
                    //
                    // first approach is simpler, so I will implement that first. if the first approach turns out to be not good enough, then
                    // I will try the second approach which will be more complex but quicker
                    var hasFix = await ContainsAnyFix(document, diagnostic, cancellationToken).ConfigureAwait(false);
                    if (hasFix)
                    {
                        return new FirstDiagnosticResult(!fullResult, hasFix, diagnostic);
                    }
                }

                return new FirstDiagnosticResult(!fullResult, false, default(DiagnosticData));
            }
        }

        public async Task<IEnumerable<CodeFixCollection>> GetFixesAsync(Document document, TextSpan range, CancellationToken cancellationToken)
        {
            // REVIEW: this is the first and simplest design. basically, when ctrl+. is pressed, it asks diagnostic service to give back
            // current diagnostics for the given span, and it will use that to get fixes. internally diagnostic service will either return cached information 
            // (if it is up-to-date) or synchronously do the work at the spot.
            //
            // this design's weakness is that each side don't have enough information to narrow down works to do. it will most likely always do more works than needed.
            // sometimes way more than it is needed. (compilation)
            Dictionary<TextSpan, List<DiagnosticData>> aggregatedDiagnostics = null;
            foreach (var diagnostic in await _diagnosticService.GetDiagnosticsForSpanAsync(document, range, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                aggregatedDiagnostics = aggregatedDiagnostics ?? new Dictionary<TextSpan, List<DiagnosticData>>();
                aggregatedDiagnostics.GetOrAdd(diagnostic.TextSpan, _ => new List<DiagnosticData>()).Add(diagnostic);
            }

            var result = new List<CodeFixCollection>();
            if (aggregatedDiagnostics != null)
            {
                foreach (var spanAndDiagnostic in aggregatedDiagnostics)
                {
                    result = await AppendFixesAsync(document, spanAndDiagnostic.Key, spanAndDiagnostic.Value, result, cancellationToken).ConfigureAwait(false);
                }
            }

            if (result.Any())
            {
                // sort the result to the order defined by the fixers
                var priorityMap = _fixerPriorityMap[document.Project.Language].Value;
                result.Sort((d1, d2) => priorityMap.ContainsKey((CodeFixProvider)d1.Provider) ? (priorityMap.ContainsKey((CodeFixProvider)d2.Provider) ? priorityMap[(CodeFixProvider)d1.Provider] - priorityMap[(CodeFixProvider)d2.Provider] : -1) : 1);
            }

            if (aggregatedDiagnostics != null)
            {
                foreach (var spanAndDiagnostic in aggregatedDiagnostics)
                {
                    result = await AppendSuppressionsAsync(document, spanAndDiagnostic.Key, spanAndDiagnostic.Value, result, cancellationToken).ConfigureAwait(false);
                }
            }

            return result;
        }

        private async Task<List<CodeFixCollection>> AppendFixesAsync(
            Document document,
            TextSpan span,
            IEnumerable<DiagnosticData> diagnosticDataCollection,
            List<CodeFixCollection> result,
            CancellationToken cancellationToken)
        {
            Lazy<ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>>> fixerMap;
            bool hasAnySharedFixer = _workspaceFixersMap.TryGetValue(document.Project.Language, out fixerMap);

            var projectFixersMap = GetProjectFixers(document.Project);
            var hasAnyProjectFixer = projectFixersMap.Any();

            if (!hasAnySharedFixer && !hasAnyProjectFixer)
            {
                return result;
            }

            ImmutableArray<CodeFixProvider> workspaceFixers;
            List<CodeFixProvider> projectFixers;
            var allFixers = new List<CodeFixProvider>();

            foreach (var diagnosticId in diagnosticDataCollection.Select(d => d.Id).Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (hasAnySharedFixer && fixerMap.Value.TryGetValue(diagnosticId, out workspaceFixers))
                {
                    allFixers.AddRange(workspaceFixers);
                }

                if (hasAnyProjectFixer && projectFixersMap.TryGetValue(diagnosticId, out projectFixers))
                {
                    allFixers.AddRange(projectFixers);
                }
            }

            var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            var extensionManager = document.Project.Solution.Workspace.Services.GetService<IExtensionManager>();
            var diagnostics = diagnosticDataCollection.Select(data => data.ToDiagnostic(tree)).ToImmutableArray();

            foreach (var fixer in allFixers.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();

                Func<Diagnostic, bool> hasFix = (d) => this.GetFixableDiagnosticIds(fixer).Contains(d.Id);
                Func<ImmutableArray<Diagnostic>, Task<IEnumerable<CodeFix>>> getFixes =
                    async (dxs) =>
                    {
                        var fixes = new List<CodeFix>();
                        var context = new CodeFixContext(document, span, dxs,

                            // TODO: Can we share code between similar lambdas that we pass to this API in BatchFixAllProvider.cs, CodeFixService.cs and CodeRefactoringService.cs?
                            (a, d) =>
                            {
                                // Serialize access for thread safety - we don't know what thread the fix provider will call this delegate from.
                                lock (fixes)
                                {
                                    fixes.Add(new CodeFix(a, d));
                                }
                            },
                            verifyArguments: false,
                            cancellationToken: cancellationToken);

                        var task = fixer.RegisterCodeFixesAsync(context) ?? SpecializedTasks.EmptyTask;
                        await task.ConfigureAwait(false);
                        return fixes;
                    };
                await AppendFixesOrSuppressionsAsync(document, span, diagnostics, result, fixer,
                    extensionManager, hasFix, getFixes, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        private async Task<List<CodeFixCollection>> AppendSuppressionsAsync(
            Document document, TextSpan span, IEnumerable<DiagnosticData> diagnosticDataCollection, List<CodeFixCollection> result, CancellationToken cancellationToken)
        {
            Lazy<ISuppressionFixProvider> lazySuppressionProvider;
            if (!_suppressionProvidersMap.TryGetValue(document.Project.Language, out lazySuppressionProvider) || lazySuppressionProvider.Value == null)
            {
                return result;
            }

            var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            var extensionManager = document.Project.Solution.Workspace.Services.GetService<IExtensionManager>();
            var diagnostics = diagnosticDataCollection.Select(data => data.ToDiagnostic(tree));

            Func<Diagnostic, bool> hasFix = (d) => lazySuppressionProvider.Value.CanBeSuppressed(d);
            Func<ImmutableArray<Diagnostic>, Task<IEnumerable<CodeFix>>> getFixes = (dxs) => lazySuppressionProvider.Value.GetSuppressionsAsync(document, span, dxs, cancellationToken);
            await AppendFixesOrSuppressionsAsync(document, span, diagnostics, result, lazySuppressionProvider.Value, extensionManager, hasFix, getFixes, cancellationToken).ConfigureAwait(false);
            return result;
        }

        private async Task<List<CodeFixCollection>> AppendFixesOrSuppressionsAsync(
            Document document,
            TextSpan span,
            IEnumerable<Diagnostic> diagnosticsWithSameSpan,
            List<CodeFixCollection> result,
            object fixer,
            IExtensionManager extensionManager,
            Func<Diagnostic, bool> hasFix,
            Func<ImmutableArray<Diagnostic>, Task<IEnumerable<CodeFix>>> getFixes,
            CancellationToken cancellationToken)
        {
            var diagnostics = diagnosticsWithSameSpan.Where(d => hasFix(d)).OrderByDescending(d => d.Severity).ToImmutableArray();
            if (diagnostics.Length <= 0)
            {
                // this can happen for suppression case where all diagnostics can't be suppressed
                return result;
            }

            var fixes = await extensionManager.PerformFunctionAsync(fixer, () => getFixes(diagnostics)).ConfigureAwait(false);
            if (fixes != null && fixes.Any())
            {
                FixAllCodeActionContext fixAllContext = null;
                var codeFixProvider = fixer as CodeFixProvider;
                if (codeFixProvider != null)
                {
                    // If the codeFixProvider supports fix all occurrences, then get the corresponding FixAllProviderInfo and fix all context.
                    var fixAllProviderInfo = ImmutableInterlocked.GetOrAdd(ref _fixAllProviderMap, codeFixProvider, FixAllProviderInfo.Create);
                    if (fixAllProviderInfo != null)
                    {
                        fixAllContext = new FixAllCodeActionContext(document, fixAllProviderInfo, codeFixProvider, diagnostics, this.GetDocumentDiagnosticsAsync, this.GetProjectDiagnosticsAsync, cancellationToken);
                    }
                }

                result = result ?? new List<CodeFixCollection>();
                var codeFix = new CodeFixCollection(fixer, span, fixes, fixAllContext);
                result.Add(codeFix);
            }

            return result;
        }

        private async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, ImmutableHashSet<string> diagnosticIds, CancellationToken cancellationToken)
        {
            Contract.ThrowIfNull(document);
            var solution = document.Project.Solution;
            var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            var diagnostics = await _diagnosticService.GetDiagnosticsForIdsAsync(solution, null, document.Id, diagnosticIds, cancellationToken).ConfigureAwait(false);
            Contract.ThrowIfFalse(diagnostics.All(d => d.DocumentId != null));
            return diagnostics.Select(d => d.ToDiagnostic(tree));
        }

        private async Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, bool includeAllDocumentDiagnostics, ImmutableHashSet<string> diagnosticIds, CancellationToken cancellationToken)
        {
            Contract.ThrowIfNull(project);

            var diagnostics = await _diagnosticService.GetProjectDiagnosticsForIdsAsync(project.Solution, project.Id, diagnosticIds, cancellationToken: cancellationToken).ConfigureAwait(false);
            Contract.ThrowIfFalse(diagnostics.All(d => d.DocumentId == null));
            var dxs = diagnostics.Select(d => d.ToDiagnostic(null));

            if (includeAllDocumentDiagnostics)
            {
                var list = new List<Diagnostic>();
                list.AddRange(dxs);

                foreach (var document in project.Documents)
                {
                    var docDiagnsotics = await GetDocumentDiagnosticsAsync(document, diagnosticIds, cancellationToken).ConfigureAwait(false);
                    list.AddRange(docDiagnsotics);
                }

                return list;
            }
            else
            {
                return dxs;
            }
        }

        private async Task<bool> ContainsAnyFix(Document document, DiagnosticData diagnostic, CancellationToken cancellationToken)
        {
            // TODO: We don't return true here if the only available fixes are suppressions.
            // This is to avoid the problem where lightbulb would show up for every green warning
            // squiggle in the editor thereby diluting the promise of the light bulb from
            // "I have a fix" to "I have some action". This is temporary until the editor team exposes
            // some mechanism (e.g. a faded out lightbulb) that would allow us to say "I have an action
            // but not a fix".

            ImmutableArray<CodeFixProvider> workspaceFixers = ImmutableArray<CodeFixProvider>.Empty;
            List<CodeFixProvider> projectFixers = null;

            Lazy<ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>>> fixerMap;
            bool hasAnySharedFixer = _workspaceFixersMap.TryGetValue(document.Project.Language, out fixerMap) && fixerMap.Value.TryGetValue(diagnostic.Id, out workspaceFixers);
            var hasAnyProjectFixer = GetProjectFixers(document.Project).TryGetValue(diagnostic.Id, out projectFixers);

            if (!hasAnySharedFixer && !hasAnyProjectFixer)
            {
                return false;
            }

            var allFixers = ImmutableArray<CodeFixProvider>.Empty;
            if (hasAnySharedFixer)
            {
                allFixers = workspaceFixers;
            }

            if (hasAnyProjectFixer)
            {
                allFixers = allFixers.AddRange(projectFixers);
            }

            var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            var dx = diagnostic.ToDiagnostic(tree);

            var extensionManager = document.Project.Solution.Workspace.Services.GetService<IExtensionManager>();

            var fixes = new List<CodeFix>();
            var context = new CodeFixContext(document, dx,

                // TODO: Can we share code between similar lambdas that we pass to this API in BatchFixAllProvider.cs, CodeFixService.cs and CodeRefactoringService.cs?
                (a, d) =>
                {
                    // Serialize access for thread safety - we don't know what thread the fix provider will call this delegate from.
                    lock (fixes)
                    {
                        fixes.Add(new CodeFix(a, d));
                    }
                },
                verifyArguments: false,
                cancellationToken: cancellationToken);

            // we do have fixer. now let's see whether it actually can fix it
            foreach (var fixer in allFixers)
            {
                await extensionManager.PerformActionAsync(fixer, () => fixer.RegisterCodeFixesAsync(context) ?? SpecializedTasks.EmptyTask).ConfigureAwait(false);
                if (!fixes.Any())
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static readonly Func<DiagnosticId, List<CodeFixProvider>> s_createList = _ => new List<CodeFixProvider>();

        private ImmutableArray<DiagnosticId> GetFixableDiagnosticIds(CodeFixProvider fixer)
        {
            return ImmutableInterlocked.GetOrAdd(ref _fixerToFixableIdsMap, fixer, f => f.FixableDiagnosticIds);
        }

        private ImmutableDictionary<LanguageKind, Lazy<ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>>>> GetFixerPerLanguageMap(
            Dictionary<LanguageKind, List<Lazy<CodeFixProvider, CodeChangeProviderMetadata>>> fixersPerLanguage)
        {
            var fixerMap = ImmutableDictionary.Create<LanguageKind, Lazy<ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>>>>();
            foreach (var languageKindAndFixers in fixersPerLanguage)
            {
                var lazyMap = new Lazy<ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>>>(() =>
                {
                    var mutableMap = new Dictionary<DiagnosticId, List<CodeFixProvider>>();

                    foreach (var fixer in languageKindAndFixers.Value)
                    {
                        foreach (var id in this.GetFixableDiagnosticIds(fixer.Value))
                        {
                            if (string.IsNullOrWhiteSpace(id))
                            {
                                continue;
                            }

                            var list = mutableMap.GetOrAdd(id, s_createList);
                            list.Add(fixer.Value);
                        }
                    }

                    var immutableMap = ImmutableDictionary.CreateBuilder<DiagnosticId, ImmutableArray<CodeFixProvider>>();
                    foreach (var diagnosticIdAndFixers in mutableMap)
                    {
                        immutableMap.Add(diagnosticIdAndFixers.Key, diagnosticIdAndFixers.Value.AsImmutableOrEmpty());
                    }

                    return immutableMap.ToImmutable();
                }, isThreadSafe: true);

                fixerMap = fixerMap.Add(languageKindAndFixers.Key, lazyMap);
            }

            return fixerMap;
        }

        private static ImmutableDictionary<LanguageKind, Lazy<ISuppressionFixProvider>> GetSupressionProvidersPerLanguageMap(
            Dictionary<LanguageKind, List<Lazy<ISuppressionFixProvider, CodeChangeProviderMetadata>>> suppressionProvidersPerLanguage)
        {
            var suppressionFixerMap = ImmutableDictionary.Create<LanguageKind, Lazy<ISuppressionFixProvider>>();
            foreach (var languageKindAndFixers in suppressionProvidersPerLanguage)
            {
                var suppressionFixerLazyMap = new Lazy<ISuppressionFixProvider>(() => languageKindAndFixers.Value.SingleOrDefault().Value);
                suppressionFixerMap = suppressionFixerMap.Add(languageKindAndFixers.Key, suppressionFixerLazyMap);
            }

            return suppressionFixerMap;
        }

        private static ImmutableDictionary<LanguageKind, Lazy<ImmutableDictionary<CodeFixProvider, int>>> GetFixerPriorityPerLanguageMap(
            Dictionary<LanguageKind, List<Lazy<CodeFixProvider, CodeChangeProviderMetadata>>> fixersPerLanguage)
        {
            var languageMap = ImmutableDictionary.CreateBuilder<LanguageKind, Lazy<ImmutableDictionary<CodeFixProvider, int>>>();
            foreach (var languageAndFixers in fixersPerLanguage)
            {
                var lazyMap = new Lazy<ImmutableDictionary<CodeFixProvider, int>>(() =>
                {
                    var priorityMap = ImmutableDictionary.CreateBuilder<CodeFixProvider, int>();

                    var fixers = ExtensionOrderer.Order(languageAndFixers.Value);
                    for (var i = 0; i < fixers.Count; i++)
                    {
                        priorityMap.Add(fixers[i].Value, i);
                    }

                    return priorityMap.ToImmutable();
                }, isThreadSafe: true);

                languageMap.Add(languageAndFixers.Key, lazyMap);
            }

            return languageMap.ToImmutable();
        }

        private ImmutableDictionary<DiagnosticId, List<CodeFixProvider>> GetProjectFixers(Project project)
        {
            return _projectFixersMap.GetValue(project.AnalyzerReferences, pId => ComputeProjectFixers(project));
        }

        private ImmutableDictionary<DiagnosticId, List<CodeFixProvider>> ComputeProjectFixers(Project project)
        {
            ImmutableDictionary<DiagnosticId, List<CodeFixProvider>>.Builder builder = null;
            foreach (var reference in project.AnalyzerReferences)
            {
                var projectCodeFixerProvider = _analyzerReferenceToFixersMap.GetValue(reference, _createProjectCodeFixProvider);
                foreach (var fixer in projectCodeFixerProvider.GetFixers(project.Language))
                {
                    var fixableIds = this.GetFixableDiagnosticIds(fixer);
                    foreach (var id in fixableIds)
                    {
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            continue;
                        }

                        builder = builder ?? ImmutableDictionary.CreateBuilder<DiagnosticId, List<CodeFixProvider>>();
                        var list = builder.GetOrAdd(id, s_createList);
                        list.Add(fixer);
                    }
                }
            }

            if (builder == null)
            {
                return ImmutableDictionary<DiagnosticId, List<CodeFixProvider>>.Empty;
            }

            return builder.ToImmutable();
        }
    }
}
