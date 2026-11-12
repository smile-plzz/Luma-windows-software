# Luma Accessibility Guide and Checklist

Role: UXD (UX Polish and Accessibility)
Date: 2026-11-11
Target Release: v0.7.0

---

## 1. Overview and Standards

Luma is committed to inclusive design for all users on Windows 10 and Windows 11, aligning with WCAG 2.1 Level AA and native Windows accessibility principles.

Key goals:
- Complete keyboard accessibility for every feature without requiring a mouse.
- Meaningful, concise AutomationProperties labels for screen readers (Narrator, NVDA, JAWS).
- Flawless readability in Windows High Contrast / Contrast Themes (Aquatic, Desert, Dusk, Night Sky).
- Fluid scaling support up to 200% text size and high-DPI displays.

---

## 2. Keyboard Navigation Reference

Luma supports full keyboard control across all interactive views:

| Shortcut | Action | Scope |
|---|---|---|
| `F1` | Open Keyboard shortcuts & help dialog | Global |
| `Space` | Toggle Quick Preview overlay (outside Select mode) | Gallery |
| `Space` / `Esc` | Close Quick Preview / Map overlay or Clear current selection | Global |
| `Enter` | Open selected media in default Windows handler (plays videos) | Gallery / Preview |
| `Left` / `Right` Arrow | Previous / Next media item | Preview Overlay |
| `F` / `T` / `M` / `I` | Toggle Favorite / Edit tags / Move to folder / Details sidebar | Preview Overlay |
| `Ctrl+B` | Toggle collapsible navigation sidebar | Global |
| `Ctrl+H` | Toggle compact/clean gallery header mode | Global |
| `Alt+Left` | Navigate back to previous browsing view | Global |
| `Home` / `End` | Scroll to top or bottom of loaded gallery | Gallery |
| `Ctrl+F` | Focus Search Box and select all text | Global |
| `F5` | Refresh current connected source | Global |
| `F2` | Rename single item or Batch Rename selected files | Gallery |
| `Delete` | Send selected original files to Windows Recycle Bin | Gallery |
| `Ctrl+C` | Copy selected originals | Gallery |
| `Ctrl+X` | Cut selected originals | Gallery |
| `Ctrl+V` | Paste cut or copied originals to destination folder | Global |
| `Ctrl+A` | Select loaded gallery items | Gallery |
| `Ctrl+mouse wheel` | Resize gallery thumbnail cards dynamically | Gallery |

---

## 3. Screen Reader and UI Automation (UIA)

### 3.1. AutomationProperties Audit
All interactive buttons, toggles, text boxes, and sliders specify:
- `AutomationProperties.Name`: Clean descriptive title read aloud by Narrator.
- `AutomationProperties.HelpText`: Contextual explanation of the action and safety guarantees (e.g. "Originals stay where they are").
- `AutomationProperties.AutomationId`: Unique identifier for automated UI testing and smoke scripts.

### 3.2. Examples
- **Add Source Button:** `AutomationProperties.Name="Add folder or drive"`, `AutomationProperties.HelpText="Add an existing folder or drive. Originals stay where they are."`
- **Search Box:** `AutomationProperties.Name="Search library"`, `AutomationProperties.HelpText="Search saved filenames, folders and tags without scanning your drive."`
- **Album Filter:** `AutomationProperties.Name="Album filter"`, `AutomationProperties.HelpText="Browse a virtual album. Drag selected media here to add it."`

---

## 4. Visual Themes, Contrast, and Scaling

### 4.1. High Contrast Support
- WinUI 3 controls automatically inherit Windows system contrast colors (`SystemColorWindowTextColor`, `SystemColorHighlightColor`).
- Custom borders, focus rectangles, and selection badges use system theme resource brushes with minimum 4.5:1 contrast ratios.

### 4.2. High DPI and Text Scaling
- Layouts employ flexible grids and stack panels that adjust dynamically when Windows Text Size is scaled up to 200%.
- Thumbnail tiles preserve aspect ratio and maintain clear spacing regardless of display scale factor (100%, 125%, 150%, 200%).

