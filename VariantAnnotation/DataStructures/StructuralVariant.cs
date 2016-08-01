﻿using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using VariantAnnotation.Algorithms;
using VariantAnnotation.DataStructures.JsonAnnotations;
using VariantAnnotation.FileHandling;
using VariantAnnotation.Interface;

namespace VariantAnnotation.DataStructures
{
    public class StructuralVariant
    {
        #region members

        public int MinBegin { get; private set; }
        public int MaxEnd { get; private set; }

        private const string SvTypeTag = "SVTYPE";
        private const string SvLengthTag = "SVLEN";
        private const string EndTag = "END";

        private const string CopyNumberTag = "CN";

        private const string TandemDuplicationAltAllele = "<DUP:TANDEM>";

        private const string AluElement = "ALU";
        private const string CopyNumberVariation = "CNV";
        private const string Deletion = "DEL";
        private const string Duplication = "DUP";
        private const string Insertion = "INS";
        private const string Inversion = "INV";
        private const string Line1Element = "LINE1";
        private const string LossOfHeterozygosity = "LOH";
        private const string SvaElement = "SVA";
        private const string TandemDuplication = "TDUP";
        private const string TranslocationBreakEnd = "BND";

        private readonly int? _svLength;
        private readonly int? _svEnd;

        private string _copyNumber;
        private readonly BreakEnd _breakEnd;
        private readonly string _referenceName;

        private readonly VariantType _variantType;
        private string _altAllele;

        #endregion

        // constructor
        public StructuralVariant(string[] vcfColumns, int minBegin, int maxEnd)
        {
            MinBegin = minBegin;
            MaxEnd = maxEnd;
            _referenceName = vcfColumns[VcfCommon.ChromIndex];

            // create a dictionary containing all of the INFO fields
            var infoKeyValues = VcfField.GetKeysAndValues(vcfColumns[VcfCommon.InfoIndex]);

            // extract the values
            var svType = ExtractValue(infoKeyValues, SvTypeTag);
            _svLength = ExtractIntValue(infoKeyValues, SvLengthTag);
            _svEnd = ExtractIntValue(infoKeyValues, EndTag);

            // check for tandem duplications
            if ((svType == Duplication) && (vcfColumns[VcfCommon.AltIndex] == TandemDuplicationAltAllele))
            {
                svType = TandemDuplication;
            }

            // assign variant types
            _variantType = GetVariantType(svType);

            // assign alt alleles
            GetAltAllele(vcfColumns, svType);

            // check for breakends
            if (_variantType == VariantType.translocation_breakend)
            {
                _breakEnd = new BreakEnd(
                    vcfColumns[VcfCommon.ChromIndex],
                    vcfColumns[VcfCommon.PosIndex],
                    vcfColumns[VcfCommon.RefIndex],
                    _altAllele);
            }
        }

        private void GetAltAllele(string[] vcfColumns, string svType)
        {
            switch (svType)
            {
                case AluElement:
                    _altAllele = "ALU";
                    break;
                case LossOfHeterozygosity:
                case CopyNumberVariation:
                    _altAllele = "CNV";
                    ExtractCopyNumber(vcfColumns);
                    break;
                case Deletion:
                    _altAllele = "deletion";
                    break;
                case Duplication:
                    _altAllele = "duplication";
                    break;
                case Insertion:
                    _altAllele = "insertion";
                    break;
                case Inversion:
                    _altAllele = "inversion";
                    break;
                case Line1Element:
                    _altAllele = "LINE1";
                    break;
                case SvaElement:
                    _altAllele = "SVA";
                    break;
                case TandemDuplication:
                    _altAllele = "tandem_duplication";
                    break;
                case TranslocationBreakEnd:
                    _altAllele = vcfColumns[VcfCommon.AltIndex];
                    break;
            }
        }

        private static VariantType GetVariantType(string svType)
        {
            VariantType variantType;

            switch (svType)
            {
                case AluElement:
                    variantType = VariantType.mobile_element_insertion;
                    break;
                case CopyNumberVariation:
                    variantType = VariantType.copy_number_variation;
                    break;
                case Deletion:
                    variantType = VariantType.deletion;
                    break;
                case Duplication:
                    variantType = VariantType.duplication;
                    break;
                case Insertion:
                    variantType = VariantType.insertion;
                    break;
                case Inversion:
                    variantType = VariantType.inversion;
                    break;
                case Line1Element:
                    variantType = VariantType.mobile_element_insertion;
                    break;
                case LossOfHeterozygosity:
                    variantType = VariantType.copy_number_variation;
                    break;
                case SvaElement:
                    variantType = VariantType.mobile_element_insertion;
                    break;
                case TandemDuplication:
                    variantType = VariantType.tandem_duplication;
                    break;
                case TranslocationBreakEnd:
                    variantType = VariantType.translocation_breakend;
                    break;
                default:
                    variantType = VariantType.unknown;
                    break;
            }
            return variantType;
        }

        /// <summary>
        /// assigns a variant type to the structural variant alternate allele
        /// </summary>
        public void AssignVariantType(VariantAlternateAllele altAllele, IAlleleTrimmer alleleTrimmer)
        {
            // sanity check: ignore unknown SV variant types
            if (_variantType == VariantType.unknown)
            {
                // the only thing we do for unknown SV types is to increment the begin if it has a symbolic allele
                if (altAllele.IsSymbolicAllele) altAllele.ReferenceBegin++;
                return;
            }

            var oldAltAllele = altAllele.AlternateAllele;
            bool has1KgCnv = false;

            // add the copy number if applicable
            if (_variantType == VariantType.copy_number_variation)
            {
                has1KgCnv = altAllele.IsSymbolicAllele && (_copyNumber == "?");
                altAllele.CopyNumber = has1KgCnv
                    ? ExtractCopyNumberFromAltAllele(oldAltAllele)
                    : _copyNumber;
            }

            altAllele.IsStructuralVariant = true;
            altAllele.VepVariantType = _variantType;
            altAllele.NirvanaVariantType = _variantType;
            altAllele.AlternateAllele = has1KgCnv ? ExtractCnvSymbolicAllele(oldAltAllele) : _altAllele;

            // handle breakends
            if (_variantType == VariantType.translocation_breakend) altAllele.BreakEnd = _breakEnd;

            // update the reference begin position
            //
            // NOTE: If any of the ALT alleles is a symbolic allele (an angle-bracketed
            //       ID String “<ID>”) then the padding base is required and POS denotes
            //       the coordinate of the base preceding the polymorphism.
            if (altAllele.IsSymbolicAllele) altAllele.ReferenceBegin++;
            else
            {
                // if the alt allele is not symbolic and its not a breakend, we call the regular trimmer
                if (_variantType != VariantType.translocation_breakend)
                {
                    altAllele.AlternateAllele = oldAltAllele;
                    alleleTrimmer.Trim(new List<VariantAlternateAllele> { altAllele });
                }
            }

            // adjust the end coordinates after adjusted the begin
            if (_svEnd != null)
            {
                altAllele.ReferenceEnd = (int)_svEnd;
                MaxEnd = Math.Max(MaxEnd, (int)_svEnd);
            }
            else if (_svLength != null)
            {
                altAllele.ReferenceEnd = altAllele.ReferenceBegin + Math.Abs(_svLength.Value) - 1;
                MaxEnd = Math.Max(MaxEnd, (int)_svLength);
            }

            // set the VID
            altAllele.VariantId = VID.Create(_referenceName, altAllele);
        }

        /// <summary>
        /// extracts the copy number from the CANVAS VCF entry
        /// </summary>
        private void ExtractCopyNumber(string[] vcfColumns)
        {
            _copyNumber = "?";

            // sanity check: skip entries that don't have one of the genotype columns
            if (vcfColumns.Length <= VcfCommon.FormatIndex) return;

            // extract the format tags
            var formalCols = vcfColumns[VcfCommon.FormatIndex].Split(':');
            string[] sampleCols = null;

            for (int i = VcfCommon.MinNumColumnsSampleGenotypes - 1; i < vcfColumns.Length; i++)
            {
                if (vcfColumns[i] != ".")
                {
                    sampleCols = vcfColumns[i].Split(':');
                }
            }

            bool foundCopyNumberIndex = false;
            int copyNumberIndex = 0;
            for (int colIndex = 0; colIndex < formalCols.Length; colIndex++)
            {
                if (formalCols[colIndex] == CopyNumberTag)
                {
                    foundCopyNumberIndex = true;
                    copyNumberIndex = colIndex;
                    break;
                }
            }

            if (sampleCols != null)
                if (foundCopyNumberIndex && (sampleCols.Length > copyNumberIndex))
                {
                    _copyNumber = sampleCols[copyNumberIndex];
                }

        }

        /// <summary>
        /// extracts the copy number from a 1000G entry. E.g. the alternate
        /// allele is in the form of "(less than)CN0(greater than)"
        /// </summary>
        private string ExtractCopyNumberFromAltAllele(string altAllele)
        {
            var altAlleleRegex = new Regex(@"<CN(\d+)>", RegexOptions.Compiled);
            var match = altAlleleRegex.Match(altAllele);
            return !match.Success ? _copyNumber : match.Groups[1].Value;
        }

        /// <summary>
        /// extracts the alternate allele without the symbolic allele characters
        /// </summary>
        private static string ExtractCnvSymbolicAllele(string altAllele)
        {
            return altAllele.Substring(1, altAllele.Length - 2);
        }

        /// <summary>
        /// returns the value if present in the dictionary, null otherwise
        /// </summary>
        private static string ExtractValue(Dictionary<string, string> infoKeyValues, string tag)
        {
            string ret;
            infoKeyValues.TryGetValue(tag, out ret);
            return ret;
        }

        /// <summary>
        /// returns the value if present in the dictionary, null otherwise
        /// </summary>
        private static int? ExtractIntValue(Dictionary<string, string> infoKeyValues, string tag)
        {
            string retString = ExtractValue(infoKeyValues, tag);
            if (retString == null) return null;

            int retNum;
            if (!int.TryParse(retString, out retNum)) return null;

            return retNum;
        }

        /// <summary>
        /// returns true if the alternate allele is a symbolic allele
        /// </summary>
        public static bool IsSymbolicAllele(string altAllele)
        {
            return altAllele.StartsWith("<") && altAllele.EndsWith(">");
        }
    }
}
