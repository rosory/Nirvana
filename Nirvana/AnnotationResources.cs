﻿using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Cloud.Messages;
using CommandLine.Utilities;
using Genome;
using IO;
using RepeatExpansions;
using VariantAnnotation;
using VariantAnnotation.Interface;
using VariantAnnotation.Interface.GeneAnnotation;
using VariantAnnotation.Interface.IO;
using VariantAnnotation.Interface.Phantom;
using VariantAnnotation.Interface.Positions;
using VariantAnnotation.Interface.Providers;
using VariantAnnotation.Providers;
using VariantAnnotation.SA;
using Vcf;
using Vcf.VariantCreator;

namespace Nirvana
{
    public sealed class AnnotationResources : IAnnotationResources
    {
        private ImmutableDictionary<IChromosome, List<int>> _variantPositions;
        public ISequenceProvider SequenceProvider { get; }
        public ITranscriptAnnotationProvider TranscriptAnnotationProvider { get; }
        private ProteinConservationProvider ProteinConservationProvider { get; }
        public IAnnotationProvider SaProvider { get; }
        public IAnnotationProvider ConservationProvider { get; }
        public IRefMinorProvider RefMinorProvider { get; }
        public IGeneAnnotationProvider GeneAnnotationProvider { get; }
        public IAnnotator Annotator { get; }
        public IRecomposer Recomposer { get; }
        public IVariantIdCreator VidCreator { get; }
        public List<IDataSourceVersion> DataSourceVersions { get; }
        public string VepDataVersion { get; }
        public long InputStartVirtualPosition { get; set; }
        public string AnnotatorVersionTag { get; set; } = "Nirvana " + CommandLineUtilities.Version;
        public bool ForceMitochondrialAnnotation { get; }
        public readonly PerformanceMetrics Metrics;

        public AnnotationResources(string refSequencePath, string inputCachePrefix, List<string> saDirectoryPaths, List<SaUrls> customAnnotations,
            string customStrTsvPath, bool disableRecomposition, bool forceMitochondrialAnnotation, bool useLegacyVids, PerformanceMetrics metrics)
        {
            Metrics = metrics;
            PerformanceMetrics.ShowInitializationHeader();
            
            SequenceProvider = ProviderUtilities.GetSequenceProvider(refSequencePath);

            var annotationFiles = new AnnotationFiles();
            saDirectoryPaths?.ForEach(x => annotationFiles.AddFiles(x));
            customAnnotations?.ForEach(x => annotationFiles.AddFiles(x));
            
            ProteinConservationProvider = ProviderUtilities.GetProteinConservationProvider(annotationFiles);
            ProteinConservationProvider?.Load();
            
            metrics.Cache.Start();
            TranscriptAnnotationProvider = ProviderUtilities.GetTranscriptAnnotationProvider(inputCachePrefix, SequenceProvider, ProteinConservationProvider);
            metrics.ShowCacheLoad();

            SaProvider             = ProviderUtilities.GetNsaProvider(annotationFiles);
            ConservationProvider   = ProviderUtilities.GetConservationProvider(annotationFiles);
            RefMinorProvider       = ProviderUtilities.GetRefMinorProvider(annotationFiles);
            GeneAnnotationProvider = ProviderUtilities.GetGeneAnnotationProvider(annotationFiles);

            var repeatExpansionProvider = new RepeatExpansionProvider(SequenceProvider.Assembly, SequenceProvider.RefNameToChromosome,
                SequenceProvider.RefIndexToChromosome.Count, customStrTsvPath);

            Annotator = new Annotator(TranscriptAnnotationProvider, SequenceProvider, SaProvider, ConservationProvider, GeneAnnotationProvider,
                repeatExpansionProvider);

            if (useLegacyVids) VidCreator = new LegacyVariantId(SequenceProvider.RefNameToChromosome);
            else VidCreator               = new VariantId(); 

            Recomposer = disableRecomposition
                ? new NullRecomposer()
                : Phantom.Recomposer.Recomposer.Create(SequenceProvider, TranscriptAnnotationProvider, VidCreator);
            DataSourceVersions = GetDataSourceVersions(TranscriptAnnotationProvider, SaProvider, GeneAnnotationProvider, ConservationProvider)
                .ToList();
            VepDataVersion = TranscriptAnnotationProvider.VepVersion + "." + CacheConstants.DataVersion + "." + SaCommon.DataVersion;

            ForceMitochondrialAnnotation = forceMitochondrialAnnotation;
        }

        private static IEnumerable<IDataSourceVersion> GetDataSourceVersions(params IProvider[] providers)
        {
            var dataSourceVersions = new List<IDataSourceVersion>();
            foreach (var provider in providers) if (provider != null) dataSourceVersions.AddRange(provider.DataSourceVersions);
            return dataSourceVersions.ToHashSet(new DataSourceVersionComparer());
        }

        public void SingleVariantPreLoad(IPosition position)
        {
            var chromToPositions = new Dictionary<IChromosome, List<int>>();
            PreLoadUtilities.TryAddPosition(chromToPositions, position.Chromosome, position.Start, position.RefAllele, position.VcfFields[VcfCommon.AltIndex], SequenceProvider.Sequence);
            _variantPositions = chromToPositions.ToImmutableDictionary();
            PreLoad(position.Chromosome);
        }

        public void GetVariantPositions(Stream vcfStream, GenomicRange genomicRange)
        {
            if (vcfStream == null)
            {
                _variantPositions = null;
                return;
            }

            vcfStream.Position = Tabix.VirtualPosition.From(InputStartVirtualPosition).BlockOffset;
            int numPositions;
            
            Metrics.SaPositionScan.Start();
            (_variantPositions, numPositions) = PreLoadUtilities.GetPositions(vcfStream, genomicRange, SequenceProvider, RefMinorProvider);
            Metrics.ShowSaPositionScanLoad(numPositions);
        }

        public void PreLoad(IChromosome chromosome)
        {
            SequenceProvider.LoadChromosome(chromosome);

            if (_variantPositions == null || !_variantPositions.TryGetValue(chromosome, out List<int> positions)) return;
            SaProvider?.PreLoad(chromosome, positions);
        }

        public void Dispose()
        {
            SequenceProvider?.Dispose();
            TranscriptAnnotationProvider?.Dispose();
            SaProvider?.Dispose();
            ConservationProvider?.Dispose();
            RefMinorProvider?.Dispose();
            GeneAnnotationProvider?.Dispose();
        }
    }
}