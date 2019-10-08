﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Genome;
using Moq;
using SAUtils.DataStructures;
using SAUtils.PrimateAi;
using VariantAnnotation.Interface.Providers;
using VariantAnnotation.Interface.SA;
using Xunit;

namespace UnitTests.SAUtils.PrimateAi
{
    public class PrimateAiTests
    {
        private static readonly IChromosome Chr10 = new Chromosome("chr10", "10", 9);
        private static readonly IChromosome Chr3 = new Chromosome("chr3", "3", 2);
        private static readonly IChromosome Chr4 = new Chromosome("chr4", "4", 3);
        private static readonly IChromosome Chr1 = new Chromosome("chr1", "1", 0);
        private static readonly IChromosome Chr21 = new Chromosome("chr21", "21", 20);
        private static ISequenceProvider GetSequenceProvider()
        {
            var refNameToChrom = new Dictionary<string, IChromosome>()
            {
                {"1", Chr1 },
                {"3", Chr3},
                {"4", Chr4 },
                {"10", Chr10},
                {"21", Chr21}
                
            };
            var refIndexToChrom = new Dictionary<ushort, IChromosome>()
            {
                { Chr1.Index, Chr1},
                { Chr3.Index, Chr3},
                { Chr4.Index, Chr4 },
                { Chr10.Index, Chr10} ,
                { Chr21.Index, Chr21},
            };

            var mockProvider = new Mock<ISequenceProvider>();
            mockProvider.SetupGet(x => x.RefNameToChromosome).Returns(refNameToChrom);
            mockProvider.SetupGet(x => x.RefIndexToChromosome).Returns(refIndexToChrom);
            return mockProvider.Object;
        }
        private static Stream GetStream()
        {
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);

            writer.WriteLine("#CHROM\tPOS\tREF\tALT\tGeneId\tScorePercentile");
            writer.WriteLine("1\t69094\tG\tA\t79501\t0.79");
            writer.WriteLine("1\t69094\tG\tC\t79501\t0.75");
            writer.WriteLine("1\t69094\tG\tT\t79501\t0.75");

            writer.WriteLine("1\t69097\tA\tG\t79501\t0.56");
            writer.WriteLine("1\t69097\tA\tC\t79501\t0.57");
            writer.WriteLine("1\t69097\tA\tT\t79501\t0.54");

            writer.WriteLine("1\t56197104\tA\tG\tENSG00000234810\t0.80");
            writer.WriteLine("1\t56197443\tC\tT\tENSG00000234810\t0.20");
            writer.WriteLine("1\t56197476\tC\tT\tENSG00000234810\t0.40");

            writer.Flush();

            stream.Position = 0;
            return stream;
        }

        [Fact]
        public void ExtractEntries()
        {
            var entrezToHgnc = new Dictionary<string, string>()
            {
                { "79501", "Gene1" },
            };

            var ensemblToHgnc = new Dictionary<string, string>()
            {
                {"ENSG00000234810", "Gene2" }
            };
            var primateParser = new PrimateAiParser(GetStream(), GetSequenceProvider(), entrezToHgnc, ensemblToHgnc);

            var items = primateParser.GetItems().ToList();

            Assert.Equal(9, items.Count);
            Assert.Equal("\"hgnc\":\"Gene1\",\"scorePercentile\":0.79", items[0].GetJsonString());
            Assert.Equal("\"hgnc\":\"Gene2\",\"scorePercentile\":0.2", items[7].GetJsonString());

        }

        private static Stream GetDuplicateItemStream()
        {
            var stream = new MemoryStream();
            using (var writer = new StreamWriter(stream, Encoding.Default, 1024, true))
            {
                writer.WriteLine("#CHROM\tPOS\tREF\tALT\tGeneId\tScorePercentile");
                writer.WriteLine("4\t155713\tA\tG\t255403\t0.03");
                writer.WriteLine("4\t155713\tA\tG\t255403\t0.93");
            }
            stream.Position = 0;
            return stream;
        }

        [Fact]
        public void ResolveDuplicates()
        {
            var entrezToHgnc = new Dictionary<string, string>()
            {
                { "255403", "Gene1"}
            };

            var ensemblToHgnc = new Dictionary<string, string>()
            {
                {"ENSG00000234810", "Gene2" }
            };
            var primateParser = new PrimateAiParser(GetDuplicateItemStream(), GetSequenceProvider(), entrezToHgnc, ensemblToHgnc);

            var items = primateParser.GetItems().Cast<ISupplementaryDataItem>().ToList();

            var deDupItems = SuppDataUtilities.DeDuplicatePrimateAiItems(items);

            Assert.Single(deDupItems);
            Assert.Equal("\"hgnc\":\"Gene1\",\"scorePercentile\":0.93", deDupItems[0].GetJsonString());

        }

    }
}