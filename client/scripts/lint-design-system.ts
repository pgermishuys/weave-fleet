#!/usr/bin/env tsx
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

interface Violation {
  category: string;
  file: string;
  line: number;
  description: string;
}

const violations: Violation[] = [];

// Recursively find all .vue files
function findVueFiles(dir: string, allowlist: string[]): string[] {
  const files: string[] = [];
  
  const entries = readdirSync(dir);
  for (const entry of entries) {
    const fullPath = join(dir, entry);
    const stat = statSync(fullPath);
    
    if (stat.isDirectory()) {
      // Skip allowlisted directories
      if (allowlist.some(allowed => fullPath.includes(allowed))) {
        continue;
      }
      files.push(...findVueFiles(fullPath, allowlist));
    } else if (entry.endsWith('.vue')) {
      files.push(fullPath);
    }
  }
  
  return files;
}

// Extract template and style blocks from Vue SFC
function extractBlocks(content: string): { template: string[], style: string[] } {
  const lines = content.split('\n');
  const template: string[] = [];
  const style: string[] = [];
  
  let inTemplate = false;
  let inStyle = false;
  let lineNumber = 0;
  
  for (const line of lines) {
    lineNumber++;
    
    if (line.match(/<template[>\s]/)) {
      inTemplate = true;
      continue;
    }
    if (line.match(/<\/template>/)) {
      inTemplate = false;
      continue;
    }
    if (line.match(/<style[>\s]/)) {
      inStyle = true;
      continue;
    }
    if (line.match(/<\/style>/)) {
      inStyle = false;
      continue;
    }
    
    if (inTemplate) {
      template.push(`${lineNumber}:${line}`);
    }
    if (inStyle) {
      style.push(`${lineNumber}:${line}`);
    }
  }
  
  return { template, style };
}

// Check for raw <button> elements
function checkRawButtons(file: string, templateLines: string[]) {
  for (const line of templateLines) {
    const [lineNum, content] = line.split(':', 2);
    
    // Skip lines with v-bind= (slot forwarding patterns)
    if (content.includes('v-bind=')) {
      continue;
    }
    
    // Check for <button
    if (content.match(/<button[\s>]/i)) {
      violations.push({
        category: 'RAW_BUTTON',
        file,
        line: parseInt(lineNum),
        description: 'Raw <button> element found. Use <Button> component instead.'
      });
    }
  }
}

// Check for hardcoded border-radius
function checkBorderRadius(file: string, styleLines: string[]) {
  for (const line of styleLines) {
    const [lineNum, content] = line.split(':', 2);
    
    // Pattern: border-radius: followed by non-zero value that isn't 50%, var(, or 0
    const match = content.match(/border-radius\s*:\s*([^;]+)/);
    if (match) {
      const value = match[1].trim();
      
      // Allow: 0, 50%, var(...)
      if (value === '0' || value.startsWith('0 ') || value.startsWith('0;')) {
        continue;
      }
      if (value === '50%' || value.startsWith('50% ')) {
        continue;
      }
      if (value.startsWith('var(')) {
        continue;
      }
      
      violations.push({
        category: 'HARDCODED_RADIUS',
        file,
        line: parseInt(lineNum),
        description: `Hardcoded border-radius: ${value}. Use design system tokens instead.`
      });
    }
  }
}

// Check for hardcoded transition timings
function checkTransitionTimings(file: string, styleLines: string[]) {
  let inKeyframes = false;
  
  for (const line of styleLines) {
    const [lineNum, content] = line.split(':', 2);
    
    // Track @keyframes blocks
    if (content.match(/@keyframes/)) {
      inKeyframes = true;
    }
    if (inKeyframes && content.match(/^\s*}\s*$/)) {
      inKeyframes = false;
    }
    
    // Skip keyframes content
    if (inKeyframes) {
      continue;
    }
    
    // Skip lines already using var(--transition...)
    if (content.match(/var\(--transition/)) {
      continue;
    }
    
    // Check for transition/animation properties with hardcoded durations
    const transitionMatch = content.match(/\b(transition|animation)(-[a-z]+)?\s*:/);
    if (transitionMatch) {
      // Look for hardcoded time values: digits followed by ms or s
      const timeMatch = content.match(/\d+(\.\d+)?(ms|s)/);
      if (timeMatch) {
        violations.push({
          category: 'HARDCODED_TIMING',
          file,
          line: parseInt(lineNum),
          description: `Hardcoded transition timing: ${timeMatch[0]}. Use design system tokens instead.`
        });
      }
    }
  }
}

// Main execution
function main() {
  const clientRoot = join(process.cwd());
  const componentsDir = join(clientRoot, 'src', 'components');
  const pluginsDir = join(clientRoot, 'src', 'plugins');
  
  // Allowlist: skip components/ui/
  const allowlist = [join(componentsDir, 'ui')];
  
  console.log('🔍 Scanning for design system violations...\n');
  
  const files = [
    ...findVueFiles(componentsDir, allowlist),
    ...findVueFiles(pluginsDir, allowlist)
  ];
  
  for (const file of files) {
    const content = readFileSync(file, 'utf-8');
    const { template, style } = extractBlocks(content);
    const relPath = relative(clientRoot, file);
    
    checkRawButtons(relPath, template);
    checkBorderRadius(relPath, style);
    checkTransitionTimings(relPath, style);
  }
  
  // Print violations
  if (violations.length > 0) {
    for (const v of violations) {
      console.log(`[${v.category}] ${v.file}:${v.line}: ${v.description}`);
    }
    
    // Summary
    console.log('\n📊 Summary:');
    const rawButtonCount = violations.filter(v => v.category === 'RAW_BUTTON').length;
    const radiusCount = violations.filter(v => v.category === 'HARDCODED_RADIUS').length;
    const timingCount = violations.filter(v => v.category === 'HARDCODED_TIMING').length;
    
    console.log(`  Raw buttons: ${rawButtonCount}`);
    console.log(`  Hardcoded border-radius: ${radiusCount}`);
    console.log(`  Hardcoded transition timings: ${timingCount}`);
    console.log(`  Total violations: ${violations.length}`);
    
    process.exit(1);
  } else {
    console.log('✅ No design system violations found!');
    process.exit(0);
  }
}

main();
