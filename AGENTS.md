# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Universal Spell Checker is a minimalist AutoHotkey script that provides instant AI-powered spell checking across all Windows applications. The focus is on maximum speed and seamless operation with minimal overhead.

## Primary Implementation

The project uses a versioned file structure with model-specific variants:

### Active Files (V5 - Current)
- **Universal Spell Checker - V5.ahk**: Latest version with all improvements (gpt-4.1 default)
- **Universal Spell Checker - V5-gpt-4.1.ahk**: Standard GPT model with `temperature` parameter
- **Universal Spell Checker - V5-gpt-5.1.ahk**: Reasoning model with `reasoning.effort:"none"`
- **Universal Spell Checker - V5-gpt-5-mini.ahk**: Reasoning model with `reasoning.effort:"minimal"`

### Stable Baseline (V4)
- **Universal Spell Checker - V4.ahk**: Stable version from before model experimentation (commit dc2c82b)

### Common Features (All Versions)
- AutoHotkey v2.0 scripts optimized for performance
- Direct OpenAI API integration via Responses API
- Instant text replacement via clipboard
- Global hotkey: Ctrl+Alt+U
- Enhanced logging with timing breakdown
- Dual JSON parsing (regex primary, Map fallback)

## Architecture & Performance

### Core Design Principles
- **Speed First**: Every operation optimized for minimal latency
- **Simplicity**: Self-contained .ahk files with no external dependencies
- **Seamless**: Direct clipboard manipulation for instant text replacement
- **Minimal**: Only essential functionality to avoid performance overhead
- **Model Flexibility**: Separate files for each model allow easy switching

### Text Processing Flow (Optimized)
1. User selects text and presses Ctrl+Alt+U
2. Script clears clipboard and copies selection (Ctrl+C)
3. Waits max 1 second for clipboard content
4. Sends text directly to OpenAI API
5. Parses response and replaces text via clipboard (Ctrl+V)

### OpenAI API Integration
- **Endpoint**: `https://api.openai.com/v1/responses` (all models use Responses API)
- **Timeout**: 30 seconds for API response
- **Prompt**: Optimized for grammar/spelling fixes while preserving formatting

#### Model-Specific Configurations

**gpt-4.1 (Standard Model)**:
- Uses `temperature` parameter (e.g., `0.3`)
- Uses `verbosity: "medium"` (does NOT support "low")
- Does NOT support `reasoning` block
- Payload: `{"model":"gpt-4.1", "input":[...], "store":true, "text":{"verbosity":"medium"}, "temperature":0.3}`

**gpt-5.1 (Reasoning Model)**:
- Uses `reasoning` block with `effort: "none"` (fastest)
- Uses `verbosity: "low"`
- Does NOT support `temperature`
- Payload: `{"model":"gpt-5.1", "input":[...], "store":true, "text":{"verbosity":"low"}, "reasoning":{"effort":"none","summary":"auto"}}`

**gpt-5-mini (Reasoning Model)**:
- Uses `reasoning` block with `effort: "minimal"` (unique to this model)
- Uses `verbosity: "low"`
- Does NOT support `temperature`
- Payload: `{"model":"gpt-5-mini", "input":[...], "store":true, "text":{"verbosity":"low"}, "reasoning":{"effort":"minimal","summary":"auto"}}`

#### Common Payload Structure
- `input`: array with `{role:"user", content:[{type:"input_text", text:"..."}]}`
- `store`: `true` (required for all models)

### Performance Optimizations
- Direct WinHTTP COM object for API calls
- **Regex-based JSON text extraction** (primary method - fastest)
  - Single RegEx match extracts corrected text directly
  - ~10x faster than full JSON object parsing
  - No object allocation or recursive traversal overhead
- **Map-based JSON parser** (fallback with debug logging)
  - Only used if regex extraction fails
  - Uses Integer()/Float() for AHK v2 compatibility
- Log API error bodies on non-200 responses for quick root-cause checks
- No file I/O or configuration overhead
- Single clipboard operation for text replacement
- Hardcoded API key to avoid configuration delays

## Critical Debugging Principles (MUST FOLLOW)

### 1. Complete Verification Before Declaring Success
**NEVER declare work complete without verifying ALL aspects, not just structure.**

When verifying API integrations or model changes:
1. Model name/identifier correct?
2. Endpoint URL correct?
3. Request structure correct and matches docs?
4. **ALL parameters supported by this specific model/type?** (Critical - often missed!)
5. Response format compatible?
6. Error handling appropriate (capture raw error body for 4xx/5xx)?

**Real Example**: When migrating to GPT-5.1, initial verification checked model name and endpoint but MISSED that reasoning models don't support the `temperature` parameter. This would have caused API errors. Always verify parameter compatibility, especially when switching model TYPES (not just versions).

**Key Learning**: Standard GPT models vs Reasoning models (GPT-5/o1/o3) have fundamentally different parameter support. Model TYPE matters, not just model name.

### 2. Debug First, Fix Second
**NEVER attempt fixes without data when root cause is unclear.**

When facing bugs:
1. **FIRST**: Add comprehensive debug logging to identify exact failure point
2. **SECOND**: Analyze debug output to understand root cause
3. **THIRD**: Implement targeted fix based on data, not assumptions

**Real Example**: Spent multiple attempts on object model issues when actual problems were:
- Regex pattern too strict (revealed by debug log: "Regex extraction returned empty")
- Number parsing syntax error (revealed by debug log: "Expected a Number but got a String at line 359")

Debug logging would have revealed both issues immediately. Always add logging FIRST.

### 3. Simplest Solution First (Performance Priority)
When user emphasizes speed/performance:
1. Consider **regex-based parsing** before object model parsing
2. Regex is ~10x faster: no object allocation, no recursive parsing, no type checking
3. For JSON responses, regex extraction is often simpler AND faster than full parsing

**Real Example**: Regex solution (`ExtractTextFromResponseRegex`) is:
- Faster: single pattern match vs recursive object traversal
- Simpler: ~25 lines vs ~200 lines of JSON parser
- More reliable: no object model version dependencies

### 4. AutoHotkey v2 Compatibility Gotchas

**Number Conversion:**
- WRONG: `return numberText + 0` (throws "Expected a Number but got a String")
- RIGHT: `return Integer(numberText)` or `return Float(numberText)`

**Object Types:**
- `obj := {}` creates basic Object
- `obj := Map()` creates Map (better for dynamic keys)
- Different versions may handle these differently

**Property Access:**
- Dot notation: `obj.property` (for known properties)
- Dynamic: `obj.%varName%` (when property name is in variable)
- Bracket: `map[key]` (works on Map, may not work on Object in some versions)

**Method Names:**
- Object: `obj.HasOwnProp(key)`
- Map: `map.Has(key)`
- Array: `arr.Length` property

### 5. Regex vs JSON Parsing Decision Tree

**Use Regex When:**
- Performance is critical
- JSON structure is consistent/predictable
- Only need to extract specific fields
- Want to avoid object model complexity

**Use JSON Parsing When:**
- Need to access multiple nested fields
- JSON structure varies
- Need to modify/rebuild JSON
- Correctness more important than speed

**This project**: Regex is correct choice (only need one text field, speed critical)

### 6. Multiple Solutions Strategy

When debugging unclear issues, prepare multiple approaches:
1. Primary solution (fastest/simplest)
2. Fallback solutions (more compatible/verbose)
3. Comprehensive logging for all paths

**Real Example**: Current implementation has:
- Primary: Regex extraction (fastest)
- Fallback: Map-based parsing with full debug logs
- Both approaches ensure we get data about what works/fails

## OpenAI Model Type Differences (CRITICAL)

### Standard GPT Models (gpt-4, gpt-4-turbo, etc.)
- Support: temperature, top_p, presence_penalty, frequency_penalty, max_tokens
- Use Chat Completions API: `/v1/chat/completions`
- Endpoint: `messages` array with role/content

### Reasoning Models (gpt-5.1, gpt-5-mini, gpt-5, o1, o3 series)
- **DO NOT support**: temperature, top_p, presence_penalty, frequency_penalty, logprobs, logit_bias
- **Only default values** or model-managed reasoning parameters
- Use Responses API: `/v1/responses`
- Endpoint: `input` array with role/content/type structure
- Use internal adaptive reasoning instead of temperature control

**Migration Checklist** (when changing models):
1. Verify model name/identifier
2. Verify correct API endpoint for that model family
3. **Verify ALL request parameters are supported** (don't assume!)
4. Check response structure differences
5. Test with sample request before declaring complete

**Why This Matters**: Reasoning models will return API errors if you send unsupported parameters like temperature or `reasoning_effort` (the correct shape is `reasoning: { effort: ... }`). Always verify parameter compatibility when switching model TYPES, not just versions; note `gpt-5-mini` uniquely allows `effort:"minimal"`.

## Important Notes for Claude

### Proactive Behavior (CRITICAL)
- **PROACTIVELY VERIFY CODE**: After making changes, ALWAYS review code for bugs without waiting for user to ask
- **PROACTIVELY UPDATE DOCUMENTATION**: When file structure or configurations change, update CLAUDE.md immediately
- **ASK CLARIFYING QUESTIONS FIRST**: When tasks have ambiguity (which version? which approach?), ask BEFORE doing work

### Model Configuration Rules
- **gpt-4.1** is a STANDARD model: uses `temperature`, `verbosity:"medium"`, NO reasoning block
- **gpt-5.1** is a REASONING model: uses `reasoning.effort:"none"`, `verbosity:"low"`, NO temperature
- **gpt-5-mini** is a REASONING model: uses `reasoning.effort:"minimal"` (unique), `verbosity:"low"`, NO temperature
- **NEVER mix parameters**: temperature and reasoning are mutually exclusive based on model type

### File Structure Awareness
- Project uses versioned files: V4 (stable baseline), V5 (current with variants)
- Model-specific variants exist: V5-gpt-4.1, V5-gpt-5.1, V5-gpt-5-mini
- When modifying code, ensure ALL relevant variant files are updated consistently
- V4 is preserved as stable baseline - do not modify unless explicitly requested

### Verification Standards
- **VERIFY EVERYTHING**: Don't declare work complete without checking ALL parameters, not just structure
- **Model type awareness**: Standard GPT vs Reasoning models have different parameter support - ALWAYS CHECK
- **SPEED IS PARAMOUNT**: Always prioritize performance - user has emphasized this repeatedly
- **Debug first**: If you can't test the code yourself, add comprehensive logging before attempting fixes (include raw error body on failures)
- **Simplest wins**: Regex > Object parsing for simple extraction tasks
- **Version awareness**: AHK v2 syntax differs from v1; use Integer()/Float() for number conversion
- **Official docs only**: When user emphasizes official documentation, be strategic in searches when direct access fails
- The scripts are intentionally minimal - but temporary debug logging is acceptable for troubleshooting
- Focus on the .ahk files, not the abandoned C# application in the App folder

## Legacy Components (Deprecated)

- **Universal Spell Checker App/**: Abandoned C# .NET implementation
- **Universal Spell Check - V1 - OG.ahk**: Original slower version
- **Universal Spell Checker - V2.ahk**: Early iteration
- **Universal Spell Checker - V3.ahk / V3.5.ahk**: Intermediate versions
- **spellcheck.js / spellcheck-old.js**: Old JavaScript approach

These exist for reference but are not actively developed. The focus is on V5 variants for active use and V4 as stable baseline.
