# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Universal Spell Checker is a minimalist AutoHotkey script that provides instant AI-powered spell checking across all Windows applications. The focus is on maximum speed and seamless operation with minimal overhead.

## Primary Implementation

**Universal Spell Checker.ahk**: The main AutoHotkey v2.0 script optimized for performance
- Single file containing all functionality
- Direct OpenAI API integration
- Instant text replacement via clipboard
- Global hotkey: Ctrl+Alt+U
- Minimal error handling to preserve speed

## Architecture & Performance

### Core Design Principles
- **Speed First**: Every operation optimized for minimal latency
- **Simplicity**: Single .ahk file with no external dependencies
- **Seamless**: Direct clipboard manipulation for instant text replacement
- **Minimal**: Only essential functionality to avoid performance overhead

### Text Processing Flow (Optimized)
1. User selects text and presses Ctrl+Alt+U
2. Script clears clipboard and copies selection (Ctrl+C)
3. Waits max 1 second for clipboard content
4. Sends text directly to OpenAI API
5. Parses response and replaces text via clipboard (Ctrl+V)

### OpenAI API Integration
- **Models**: Uses `gpt-4.1` or `gpt-4.1-mini` (both are real, valid OpenAI models)
- **Temperature**: 0.3 for consistent corrections
- **Timeout**: 30 seconds for API response
- **Prompt**: Optimized for grammar/spelling fixes while preserving formatting

### Performance Optimizations
- Direct WinHTTP COM object for API calls
- Minimal JSON parsing using regex
- No file I/O or configuration overhead
- Single clipboard operation for text replacement
- Hardcoded API key to avoid configuration delays

## Important Notes for Claude

- **gpt-4.1** and **gpt-4.1-mini** are legitimate OpenAI models - do not suggest they're invalid
- Prioritize performance over features - only suggest changes that maintain or improve speed
- The script is intentionally minimal - avoid adding configuration, logging, or complex error handling
- Focus on the .ahk file, not the abandoned C# application in the App folder

## Legacy Components (Deprecated)

- **Universal Spell Checker App/**: Abandoned C# .NET implementation
- **Universal Spell Check - OG.ahk**: Original slower version
- **spellcheck.js / spellcheck-old.js**: Old JavaScript approach

These exist for reference but are not actively developed. The focus is entirely on the streamlined `Universal Spell Checker.ahk` script.