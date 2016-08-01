﻿using System;
using VariantAnnotation.Algorithms.Consequences;
using VariantAnnotation.DataStructures;
using VariantAnnotation.Utilities;

namespace VariantAnnotation.Algorithms
{
    public class HgvsProteinNomenclature
    {
        #region members

        private readonly VariantEffect _variantEffect;
        private readonly Transcript _transcript;
        private readonly TranscriptAnnotation _ta;
        private readonly VariantFeature _variant;

        private readonly HgvsNotation _hgvsNotation;

        #endregion

        internal class HgvsNotation
        {
            public string ReferenceAminoAcids;
            public string AlternateAminoAcids;
            public string ReferenceAbbreviation;
            public string AlternateAbbreviation;

            public int ReferenceAminoAcidsLen;
            public int AlternateAminoAcidsLen;
            public int Start;
            public int End;
            public ProteinChange Type;
            public readonly string ProteinId;

            // constructor
            public HgvsNotation(string referenceAminoAcids, string alternateAminoAcids, string proteinId, int start, int end)
            {
                SetAminoAcids(referenceAminoAcids, ref ReferenceAminoAcids, ref ReferenceAminoAcidsLen);
                SetAminoAcids(alternateAminoAcids, ref AlternateAminoAcids, ref AlternateAminoAcidsLen);

                ProteinId = proteinId;
                Start = start;
                End = end;
            }

            public void SetReferenceAminoAcids(string aminoAcids)
            {
                SetAminoAcids(aminoAcids, ref ReferenceAminoAcids, ref ReferenceAminoAcidsLen);
            }

            public void SetAlternateAminoAcids(string aminoAcids)
            {
                SetAminoAcids(aminoAcids, ref AlternateAminoAcids, ref AlternateAminoAcidsLen);
            }

            // ReSharper disable RedundantAssignment
            private static void SetAminoAcids(string aminoAcids, ref string s, ref int len)
            // ReSharper restore RedundantAssignment
            {
                s   = aminoAcids;
                len = s?.Length ?? 0;
            }
        }

        /// <summary>
        /// constructor
        /// </summary>
        public HgvsProteinNomenclature(VariantEffect variantEffect, TranscriptAnnotation ta, Transcript transcript, VariantFeature variant)
        {
            _variantEffect = variantEffect;
            _ta            = ta;
            _transcript    = transcript;
            _variant       = variant;

            _hgvsNotation = new HgvsNotation(_ta.ReferenceAminoAcids, _ta.AlternateAminoAcids,
                FormatUtilities.GetVersion(_transcript.ProteinId, _transcript.ProteinVersion), _ta.ProteinBegin,
                _ta.ProteinEnd);
        }

        /// <summary>
        /// return a string representing the protein-level effect of this allele in HGVS format [TranscriptVariationAllele.pm:717 hgvs_protein]
        /// </summary>
        public void SetAnnotation()
        {
            // sanity check: don't try to handle odd characters, make sure this is not a reference allele, 
            //               and make sure that we have protein coordinates
            if (_variant.IsReference || !_ta.HasValidCdsEnd || !_ta.HasValidCdsEnd ||
                SequenceUtilities.HasNonCanonicalBase(_ta.TranscriptAlternateAllele))
                return;

            // check if this is a stop retained variant
            if (_variantEffect.IsStopRetained())
            {
                _ta.HgvsProteinSequenceName = $"{_ta.HgvsCodingSequenceName}(p.=)";
                return;
            }

            // clip the alleles
            AminoAcids.RemovePrefixAndSuffix(_hgvsNotation);

            // set the protein change
            _hgvsNotation.Type = GetGeneralProteinChange();

            if (_hgvsNotation.Type != ProteinChange.None)
            {
                _hgvsNotation.Type = GetSpecificProteinChange();

                // convert ref & alt peptides taking into account HGVS rules
				GetHgvsPeptides(_ta);
            }

            // no protein change - return transcript nomenclature with flag for neutral protein consequence
            if (_hgvsNotation.Type == ProteinChange.None)
            {
                _ta.HgvsProteinSequenceName = $"{_ta.HgvsCodingSequenceName}(p.=)";
                return;
            }

            // string formatting
            _ta.HgvsProteinSequenceName = GetHgvsProteinFormat(_ta);
        }

        /// <summary>
        /// get the general protein change that resulted from this variation [TranscriptVariationAllele.pm:1339 _clip_alleles]
        /// </summary>
        private ProteinChange GetGeneralProteinChange()
        {
            if (_hgvsNotation.ReferenceAminoAcids == _hgvsNotation.AlternateAminoAcids) return ProteinChange.None;
            if ((_hgvsNotation.ReferenceAminoAcidsLen == 0) && (_hgvsNotation.AlternateAminoAcidsLen != 0)) return ProteinChange.Insertion;
            if ((_hgvsNotation.ReferenceAminoAcidsLen != 0) && (_hgvsNotation.AlternateAminoAcidsLen == 0)) return ProteinChange.Deletion;
            return ProteinChange.Unknown;
        }

        /// <summary>
        /// get the specific protein change that resulted from this variation [TranscriptVariationAllele.pm:1126 _get_hgvs_protein_type]
        /// </summary>
        private ProteinChange GetSpecificProteinChange()
        {
            // frameshifts
            if (_variantEffect.IsFrameshiftVariant()) return ProteinChange.Frameshift;

            // insertions
            if (_hgvsNotation.Type == ProteinChange.Insertion) return ProteinChange.Insertion;

            // SNVs
            if ((_hgvsNotation.ReferenceAminoAcidsLen == 1) && (_hgvsNotation.AlternateAminoAcidsLen == 1)) return ProteinChange.Substitution;

            // deletions
            if (_hgvsNotation.Type == ProteinChange.Deletion) return ProteinChange.Deletion;

            // duplications
            if ((_hgvsNotation.AlternateAminoAcidsLen > _hgvsNotation.ReferenceAminoAcidsLen) &&
                _hgvsNotation.AlternateAminoAcids.Contains(_hgvsNotation.ReferenceAminoAcids))
            {
                return ProteinChange.Duplication;
            }

            // deletions/insertions
            if (_hgvsNotation.ReferenceAminoAcidsLen != _hgvsNotation.AlternateAminoAcidsLen) return ProteinChange.InDel;

            return ProteinChange.Substitution;
        }

        /// <summary>
        /// gets the reference and alternative amino acids [TranscriptVariationAllele.pm:1204 _get_hgvs_peptides]
        /// </summary>
        private void GetHgvsPeptides(TranscriptAnnotation ta)
        {
            // frameshifts
            if (_hgvsNotation.Type == ProteinChange.Frameshift)
            {
                // original alt/ref peptides are not the same as HGVS alt/ref - look up seperately
                GetFrameshiftPeptides(ta);

                if (_hgvsNotation == null) return;
            }
            else if (_hgvsNotation.Type == ProteinChange.Insertion)
            {
				AminoAcids.Rotate3Prime(_hgvsNotation, _transcript.Peptide);
				// check that inserted bases do not duplicate 3' reference sequence [set to type = dup and return if so]
				if (IsAminoAcidDuplicate(_hgvsNotation, _transcript.Peptide)) return;

                // HGVS ref are peptides flanking insertion
                var min = Math.Min(_hgvsNotation.Start, _hgvsNotation.End);
                // the peptide positions start from 1. In case of insertions, the end might become 0
                if (min == 0) min++;
                _hgvsNotation.SetReferenceAminoAcids(GetSurroundingPeptides(min));
            }
            else if (_hgvsNotation.Type == ProteinChange.Deletion)
            {
                AminoAcids.Rotate3Prime(_hgvsNotation, _transcript.Peptide);
            }

            // set the three-letter abbreviations
            if (_hgvsNotation.ReferenceAminoAcidsLen > 0) _hgvsNotation.ReferenceAbbreviation = AminoAcids.GetAbbreviations(_hgvsNotation.ReferenceAminoAcids);
            _hgvsNotation.AlternateAbbreviation = _hgvsNotation.AlternateAminoAcidsLen == 0 ? "del" : AminoAcids.GetAbbreviations(_hgvsNotation.AlternateAminoAcids);

            // handle special cases
            if (_variantEffect.IsStartLost())
            {
                // handle initiator loss - probably no translation => alt allele is '?'
                _hgvsNotation.AlternateAbbreviation = "?";
                _hgvsNotation.Type = ProteinChange.Unknown;
            }
            else if (_hgvsNotation.Type == ProteinChange.Deletion)
            {
                _hgvsNotation.AlternateAbbreviation = "del";
            }
            else if (_hgvsNotation.Type == ProteinChange.Frameshift)
            {
                // only quote first ref peptide for frameshift
                if (_hgvsNotation.ReferenceAbbreviation != null) _hgvsNotation.ReferenceAbbreviation = _hgvsNotation.ReferenceAbbreviation.FirstAminoAcid3();
            }
        }

        /// <summary>
        /// returns true if this insertion has the same amino acids preceding it [TranscriptVariationAllele.pm:1494 _check_for_peptide_duplication]
        /// </summary>
        private static bool IsAminoAcidDuplicate(HgvsNotation hn, string transcriptPeptides)
        {
            // sanity check: return false if the alternate amino acid is null
            if (hn.AlternateAminoAcids == null) return false;

            int testAminoAcidPos = hn.Start - hn.AlternateAminoAcidsLen - 1;
            if (testAminoAcidPos < 0) return false;

            string precedingAminoAcids = testAminoAcidPos + hn.AlternateAminoAcidsLen <= transcriptPeptides.Length
                ? transcriptPeptides.Substring(testAminoAcidPos, hn.AlternateAminoAcidsLen)
                : "";

            // update our HGVS notation
            if ((testAminoAcidPos >= 0) && (precedingAminoAcids == hn.AlternateAminoAcids))
            {
                hn.Type = ProteinChange.Duplication;
                hn.End = hn.Start - 1;
                hn.Start -= hn.AlternateAminoAcidsLen;
                hn.AlternateAbbreviation = AminoAcids.GetAbbreviations(hn.AlternateAminoAcids);
                return true;
            }

            return false;
        }

        /// <summary>
        /// returns the HGVS protein string [TranscriptVariationAllele.pm:992 _get_hgvs_protein_format]
        /// </summary>
        private string GetHgvsProteinFormat(TranscriptAnnotation ta)
        {
            string ret = _hgvsNotation.ProteinId + ":p.";

            // handle stop_lost seperately regardless of cause by del/delins => p.TerposAA1extnum_AA_to_stop
            if (_variantEffect.IsStopLost())
            {
                _hgvsNotation.AlternateAbbreviation = _hgvsNotation.AlternateAbbreviation.FirstAminoAcid3();
                var translatedCds = GetTranslatedCodingSequence(ta);

                if (_hgvsNotation.Type == ProteinChange.Deletion)
                {
                    int numExtraAminoAcids = GetNumAminoAcidsUntilStopCodon(translatedCds, _hgvsNotation.Start - 1, false);
                    if (numExtraAminoAcids != -1) _hgvsNotation.AlternateAbbreviation += "extTer" + numExtraAminoAcids;
                }
                else if (_hgvsNotation.Type == ProteinChange.Substitution)
                {
                    int numExtraAminoAcids = GetNumAminoAcidsUntilStopCodon(translatedCds, _hgvsNotation.Start - 1, false);
                    if (numExtraAminoAcids != -1) _hgvsNotation.AlternateAbbreviation += "extTer" + numExtraAminoAcids;
                    else _hgvsNotation.AlternateAbbreviation += "extTer?";
                }

                return ret + _hgvsNotation.ReferenceAbbreviation + _hgvsNotation.Start + _hgvsNotation.AlternateAbbreviation;
            }

            // handle the non stop-lost cases
            switch (_hgvsNotation.Type)
            {
                case ProteinChange.Duplication:
                    if (_hgvsNotation.Start < _hgvsNotation.End)
                    {
                        var firstRefPeptide = _hgvsNotation.AlternateAbbreviation.FirstAminoAcid3();
                        var lastRefPeptide = _hgvsNotation.AlternateAbbreviation.LastAminoAcid3();
                        ret += firstRefPeptide + _hgvsNotation.Start + '_' + lastRefPeptide + _hgvsNotation.End + "dup";
                    }
                    else
                    {
                        ret += _hgvsNotation.AlternateAbbreviation + _hgvsNotation.Start + "dup";
                    }
                    break;

                case ProteinChange.Substitution:
                    ret += _hgvsNotation.ReferenceAbbreviation + _hgvsNotation.Start + _hgvsNotation.AlternateAbbreviation;
                    break;

                case ProteinChange.InDel:
                case ProteinChange.Insertion:
                    if (_hgvsNotation.AlternateAbbreviation.StartsWith("Ter"))
                        _hgvsNotation.AlternateAbbreviation = "Ter";

                    // list the first and last AA in reference only
                    var firstInsPeptide = _hgvsNotation.ReferenceAbbreviation.FirstAminoAcid3();
                    var lastInsPeptide = _hgvsNotation.ReferenceAbbreviation.LastAminoAcid3();

                    // for stops & add extX & distance to next stop to alt pep
                    if ((_hgvsNotation.ReferenceAminoAcids != null) && _hgvsNotation.ReferenceAminoAcids.EndsWith("X"))
                    {
                        var translatedCds = GetTranslatedCodingSequence(ta);
                        int numExtraAminoAcids = GetNumAminoAcidsUntilStopCodon(translatedCds, _hgvsNotation.Start - 1, false);
                        if (numExtraAminoAcids != -1) _hgvsNotation.AlternateAbbreviation += "extTer" + numExtraAminoAcids;
                    }

                    if ((_hgvsNotation.Start == _hgvsNotation.End) && (_hgvsNotation.Type == ProteinChange.InDel))
                    {
                        ret += firstInsPeptide + _hgvsNotation.Start + "delins" + _hgvsNotation.AlternateAbbreviation;
                    }
                    else
                    {
	                    if (_hgvsNotation.Start > _hgvsNotation.End)
		                    Swap.Int(ref _hgvsNotation.Start, ref _hgvsNotation.End);
	                    if (_hgvsNotation.End > _transcript.Peptide.Length)
	                    {
		                    ret = null;
	                    }
	                    else
	                    {
		                    ret += firstInsPeptide + _hgvsNotation.Start + '_' + lastInsPeptide + _hgvsNotation.End +
		                           (_hgvsNotation.Type == ProteinChange.Insertion ? "ins" : "delins") +
		                           _hgvsNotation.AlternateAbbreviation;
	                    }

					}
                    break;

                case ProteinChange.Frameshift:
                    ret += _hgvsNotation.ReferenceAbbreviation + _hgvsNotation.Start +
                           _hgvsNotation.AlternateAbbreviation;

                    if (_hgvsNotation.AlternateAbbreviation != "Ter")
                    {
                        // not immediate stop - count aa until next
                        var translatedCds = GetTranslatedCodingSequence(ta);
                        int numExtraAminoAcids = GetNumAminoAcidsUntilStopCodon(translatedCds, _hgvsNotation.Start - 1, true);
                        if (numExtraAminoAcids == -1)
                        {
                            // new - ? to show new stop not predicted
                            ret += "fsTer?";
                        }
                        else
                        {
                            // use long form if new stop found
                            ret += "fsTer" + numExtraAminoAcids;
                        }
                    }
                    break;

                case ProteinChange.Deletion:
                    if (_hgvsNotation.ReferenceAbbreviation.Length > 3)
                    {
                        var firstRefPeptide = _hgvsNotation.ReferenceAbbreviation.FirstAminoAcid3();
                        var lastRefPeptide = _hgvsNotation.ReferenceAbbreviation.LastAminoAcid3();
                        ret += firstRefPeptide + _hgvsNotation.Start + '_' + lastRefPeptide + _hgvsNotation.End + "del";
                    }
                    else
                    {
                        ret += GetHgvsRangeString(_hgvsNotation);
                    }
                    break;

                default:
                    // default to substitution
                    ret += GetHgvsRangeString(_hgvsNotation);
                    break;
            }

            return ret;
        }

        /// <summary>
        /// gets the reference and alternative amino acids for frameshifts [TranscriptVariationAllele.pm:1377 _get_fs_peptides]
        /// </summary>
        private void GetFrameshiftPeptides(TranscriptAnnotation ta)
        {
            var translatedCds = GetTranslatedCodingSequence(ta);
            if (translatedCds == null) return;
			
            var refTrans = _transcript.Peptide + '*';
            int translatedCdsLen = translatedCds.Length;

            _hgvsNotation.Start = ta.ProteinBegin;

            if (_hgvsNotation.Start > translatedCds.Length)
            {
                // TODO: careful as this might be the source of delinsdel
                _hgvsNotation.ReferenceAbbreviation = "del";
                _hgvsNotation.AlternateAbbreviation = "del";
                return;
            }

			

			while (_hgvsNotation.Start <= translatedCdsLen)
			{
				char refAminoAcid = refTrans[_hgvsNotation.Start - 1];
				char altAminoAcid = translatedCds[_hgvsNotation.Start - 1];

				// variation at stop codon, but maintains stop codon - set to synonymous
				if ((refAminoAcid == '*') && (altAminoAcid == '*'))
				{
					_hgvsNotation.Type = ProteinChange.None;
					return;
				}

				if (refAminoAcid != altAminoAcid)
				{
					_hgvsNotation.SetReferenceAminoAcids(refAminoAcid.ToString());
					_hgvsNotation.SetAlternateAminoAcids(altAminoAcid.ToString());

					break;
				}
				_hgvsNotation.SetReferenceAminoAcids(refAminoAcid.ToString());
				_hgvsNotation.SetAlternateAminoAcids(altAminoAcid.ToString());

				_hgvsNotation.Start++;
			}
		}

		/// <summary>
		/// returns the translated coding sequence including the variant and the 3' UTR
		/// </summary>
		private string GetTranslatedCodingSequence(TranscriptAnnotation ta)
        {
            // get the sequence with the variant added
            string altCds = _transcript.GetAlternateCds(ta.CodingDnaSequenceBegin, ta.CodingDnaSequenceEnd, ta.TranscriptAlternateAllele);
            if (string.IsNullOrEmpty(altCds)) return null;

            // get the new translation
            return AminoAcids.TranslateBases(altCds, true);
        }

        /// <summary>
        /// get the amino acids before and after the insertion [TranscriptVariationAllele.pm:1420 _get_surrounding_peptides]
        /// </summary>
        private string GetSurroundingPeptides(int pos)
        {
            var peptide = _transcript.Peptide;

			//removed since we do not want to change the peptide in transcript
            //if (_hgvsNotation.OriginalReferenceAminoAcids != null) peptide += _hgvsNotation.OriginalReferenceAminoAcids;

            // sanity check: make sure we have enough peptides
            if (peptide.Length < pos+1) return null;

            return peptide.Substring(pos - 1, 2);
        }

        /// <summary>
        /// returns the number of amino acids until the next stop codon is encountered [TranscriptVariationAllele.pm:1531 _stop_loss_extra_AA]
        /// </summary>
        private int GetNumAminoAcidsUntilStopCodon(string altCds, int refVarPos, bool isFrameshift)
        {
            int numExtraAminoAcids = -1;
            int refLen = _transcript.Peptide.Length;

            // sanity check: 
            if ((altCds == null) || (refVarPos > altCds.Length)) return numExtraAminoAcids;

            // find the number of residues that are translated until a termination codon is encountered
            var terPos = altCds.IndexOf('*');
            if (terPos != -1)
            {
                numExtraAminoAcids = terPos + 1 - (isFrameshift ? refVarPos : refLen + 1);
            }

            // A special case is if the first aa is a stop codon => don't display the number of residues until the stop codon
            return numExtraAminoAcids > 0 ? numExtraAminoAcids : -1;
        }

        /// <summary>
        /// returns a string with the HGVS representation for either the single position or the ranged position
        /// </summary>
        private static string GetHgvsRangeString(HgvsNotation hn)
        {
            if (hn.Start == hn.End) return hn.ReferenceAbbreviation + hn.Start + hn.AlternateAbbreviation;
            return hn.ReferenceAbbreviation + hn.Start + '_' + hn.AlternateAbbreviation + hn.End;
        }
    }

    public enum ProteinChange
    {
        Unknown,
        Deletion,
        Duplication,
        Frameshift,
        InDel,
        Insertion,
        None,
        Substitution
    }
}
