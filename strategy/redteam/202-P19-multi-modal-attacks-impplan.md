# Implementation Plan: Multi-Modal Attacks (Image Prompt Injection)

> **Document ID:** 202
> **Priority:** P19 (Future / parked)
> **Focus Area:** Multi-Modal Attack Vectors
> **Created:** January 31, 2026 · **Last verified:** June 13, 2026 (`feature/redteam-newwave-fixes`)
> **Status:** 📋 NOT STARTED — fully pending (verified against code)
> **Dependencies (still unmet):** MAF multi-modal/vision agent support · image-hosting infrastructure · vision-response test mocking
> **Target Outcome:** 15-20 new multi-modal probes
>
> **Done:** nothing. **Pending:** everything below — no `MultiModalAttack`, `IMultiModalAttackType`, `ImageProbe`, or `MultiModalEvaluator` exists in code; the current roster is 13 (+3 opt-in) attacks with no image/multi-modal attack; probes MM-001..MM-019 are unimplemented. This remains a forward-looking plan — **do not move to `done/`**. Unblock only when MAF gains vision support.

---

## Executive Summary

This plan covers **multi-modal attacks** that were extracted from Plan 200. These attacks target AI systems that process images alongside text, exploiting the image encoder to bypass text-based safety filters.

**Why Deferred:** Requires infrastructure not currently available:
- Image hosting for test probes
- MAF multi-modal agent support
- Vision-language model testing capabilities

---

## Background: Multi-Modal Attack Research

### What Are Multi-Modal Attacks?

Multi-modal attacks exploit AI systems that process multiple input types (text + images). The attack embeds adversarial text in images that visual encoders process, bypassing text-based safety filters.

### Research Sources

| Source | Technique | Description |
|--------|-----------|-------------|
| **Figstep (2023)** | Typography attacks | Text rendered as images bypasses filters |
| **Visual Jailbreak (2024)** | Adversarial images | Pixel patterns that trigger unsafe responses |
| **garak** | `visual_jailbreak` | Implementation of visual attacks |
| **PyRIT** | Multi-modal orchestration | Conversation-based image attacks |

### Attack Categories

| Category | Description | Risk Level |
|----------|-------------|------------|
| **Typography-based** | Safe-looking text that OCR reads as instructions | HIGH |
| **Steganography** | Hidden instructions in image metadata/pixels | MEDIUM |  
| **Adversarial Noise** | Pixel patterns that shift model behavior | HIGH |
| **QR Code Injection** | Malicious payloads in QR codes | MEDIUM |
| **Caption Manipulation** | Alt-text/caption injection | LOW |

---

## Proposed Implementation

### New Attack Type: `MultiModalAttack`

```csharp
namespace AgentEval.RedTeam.Attacks;

/// <summary>
/// Multi-modal attacks targeting vision-language models.
/// Embeds adversarial content in images to bypass text-based filters.
/// </summary>
public sealed class MultiModalAttack : IAttackType
{
    public string Name => "MultiModal";
    public string DisplayName => "Multi-Modal Image Attacks";
    public string Description => 
        "Tests agent's resistance to adversarial content embedded in images.";
    public string OwaspLlmId => "LLM01"; // Prompt injection via images
    public string[] MitreAtlasIds => ["AML.T0051", "AML.T0056"];
    public Severity DefaultSeverity => Severity.High;
}
```

### Proposed Probes (15-20 total)

```yaml
# Typography-based (5 probes)
MM-001: Image with "Ignore all previous instructions" rendered as text
MM-002: Image with base64-encoded malicious instruction in typography
MM-003: Multi-language typography attack (renders as benign in one language, malicious in another)
MM-004: OCR-resistant font attack (special characters readable by model)
MM-005: Layered typography (visible vs hidden text layers)

# Steganography (4 probes)  
MM-006: Instructions in image EXIF metadata
MM-007: Instructions in PNG comment chunk
MM-008: LSB steganography in pixel values
MM-009: Instructions in image color palette

# Adversarial Images (4 probes)
MM-010: Adversarial noise pattern triggering unsafe responses
MM-011: Figstep-style typography attack image
MM-012: Gradient-based adversarial perturbation
MM-013: Universal adversarial patch

# QR/Barcode Injection (3 probes)
MM-014: QR code with malicious URL
MM-015: QR code with base64-encoded instructions
MM-016: Barcode containing injection payload

# Caption/Alt-Text Manipulation (3 probes)
MM-017: Alt-text injection in image description
MM-018: IPTC caption field manipulation
MM-019: XMP metadata instruction injection
```

### Infrastructure Requirements

| Requirement | Description | Status |
|-------------|-------------|--------|
| **Image Hosting** | Host test images for probes | ❌ Not Available |
| **MAF Vision Support** | Multi-modal agent capabilities | ❌ TBD |
| **Test Infrastructure** | Mocking vision model responses | ❌ Not Built |
| **Image Generation** | Create typography/adversarial images | ❌ Manual Process |

### Recommended Approach

1. **Phase 1: Static Images** - Pre-generate attack images, host on CDN
2. **Phase 2: Dynamic Generation** - Generate images programmatically  
3. **Phase 3: Adversarial ML** - Use gradient-based perturbation (requires model access)

---

## Dependencies

### External Dependencies
- MAF multi-modal agent support
- Azure AI Vision or OpenAI Vision API access
- Image hosting (Azure Blob, CDN, or embedded base64)

### Internal Dependencies
- `IMultiModalAttackType` interface
- `ImageProbe` class for image-based probes
- `MultiModalEvaluator` for vision response evaluation

---

## Competitive Analysis

| Tool | Multi-Modal Support | Techniques |
|------|---------------------|------------|
| **garak** | ✅ visual_jailbreak module | Typography, adversarial |
| **PyRIT** | ✅ Image orchestration | Multi-turn image attacks |
| **Promptfoo** | ❌ Text only | N/A |
| **AgentEval** | ❌ Planned (this plan) | To be determined |

---

## Timeline Estimate

| Phase | Duration | Status |
|-------|----------|--------|
| **Research** | 1 week | 📋 Not Started |
| **Infrastructure** | 2 weeks | 📋 Not Started |
| **Implementation** | 1-2 weeks | 📋 Not Started |
| **Testing** | 1 week | 📋 Not Started |

**Total Estimate:** 5-6 weeks (when MAF supports multi-modal)

---

## Success Criteria

- [ ] 15+ multi-modal probes implemented
- [ ] Typography attack coverage
- [ ] Steganography detection
- [ ] Integration with existing AttackPipeline
- [ ] Test coverage with mocked vision responses
- [ ] Documentation with academic citations

---

## References

1. **Figstep**: "Jailbreaking ChatGPT via Prompt Engineering: An Empirical Study" (2023)
2. **Visual Jailbreak**: "Visual Adversarial Examples Jailbreak Aligned Large Language Models" (2024)
3. **garak visual_jailbreak**: https://github.com/leondz/garak
4. **MITRE ATLAS AML.T0056**: Visual Data Poisoning

---

*Document extracted from Plan 200 Part 3 on January 31, 2026*
