// Fetches Fluent UI System Icons (MIT) at a pinned commit and writes src/RigShift.Avalonia/Views/Icons.cs.
// node tools/fluent-icons.mjs <commit> src/RigShift.Avalonia/Views/Icons.cs [svgs.json]. A new icon: add it to `regular`.
import { writeFileSync } from 'node:fs';

const commit = process.argv[2];
const out = process.argv[3];
const regular = {
  Board: 'Board', Desktop: 'Desktop', Games: 'Games', Eye: 'Eye', Options: 'Options', QuestionCircle: 'Question Circle',
  MoreHorizontal: 'More Horizontal', ChevronDown: 'Chevron Down', ChevronRight: 'Chevron Right', Add: 'Add', Delete: 'Delete',
  ArrowUp: 'Arrow Up', ArrowDown: 'Arrow Down', ArrowRight: 'Arrow Right', Play: 'Play', Pause: 'Pause', Speaker2: 'Speaker 2',
  Apps: 'Apps', PlugConnected: 'Plug Connected', ToggleRight: 'Toggle Right', Window: 'Window', Copy: 'Copy', Dismiss: 'Dismiss',
  ArrowSwap: 'Arrow Swap', Keyboard: 'Keyboard', Archive: 'Archive', Flag: 'Flag', Open: 'Open', Pin: 'Pin', Search: 'Search',
  Checkmark: 'Checkmark', ScanDash: 'Scan Dash', ArrowCounterclockwise: 'Arrow Counterclockwise', Info: 'Info', ArrowEnter: 'Arrow Enter',
  VehicleCarProfile: 'Vehicle Car Profile', HeadsetVr: 'Headset VR', Tv: 'TV', Mic: 'Mic',
};
const filled = ['Board', 'Desktop', 'Games', 'Eye', 'Options', 'QuestionCircle', 'Play', 'Pin'];
const sizes = [16, 20, 24];

const file = (folder, size, variant) =>
  `https://raw.githubusercontent.com/microsoft/fluentui-system-icons/${commit}/assets/${encodeURIComponent(folder)}/SVG/ic_fluent_${folder.toLowerCase().replaceAll(' ', '_')}_${size}_${variant}.svg`;

async function geometry(folder, size, variant) {
  const res = await fetch(file(folder, size, variant));
  if (!res.ok) return null;
  const svg = await res.text();
  const paths = [...svg.matchAll(/<path([^>]*)>/g)].map(m => m[1]);
  if (paths.length === 0) throw new Error(`no path in ${folder} ${size} ${variant}`);
  const evenOdd = paths.some(p => /fill-rule="evenodd"/.test(p));
  if (evenOdd && paths.length > 1) throw new Error(`mixed fill rules in ${folder} ${size} ${variant}`);
  const d = paths.map(p => /\sd="([^"]+)"/.exec(p)[1]).join(' ');
  return { svg, data: `${evenOdd ? 'F0' : 'F1'} ${d}` };
}

const lines = [];
const missing = [];
const svgs = {};
for (const [name, folder] of Object.entries(regular)) {
  for (const variant of ['regular', 'filled']) {
    if (variant === 'filled' && !filled.includes(name)) continue;
    for (const size of sizes) {
      const g = await geometry(folder, size, variant);
      const member = `${name}${variant === 'filled' ? 'Filled' : ''}${size}`;
      if (!g) { missing.push(member); continue; }
      lines.push(`    public static readonly Geometry ${member} = Geometry.Parse("${g.data}");`);
      svgs[member] = g.svg;
    }
  }
}

writeFileSync(out, `using Avalonia.Media;

namespace RigShift.Avalonia.Views;

/// <summary>
/// Fluent UI System Icons (Microsoft, MIT, see THIRD-PARTY-NOTICES.md), copied from microsoft/fluentui-system-icons at
/// ${commit} by tools/fluent-icons.mjs. Each size is Microsoft's own drawing for that size, so draw it unstretched in a
/// box of the same size: the <c>icon</c> (16), <c>icon md</c> (20) and <c>icon lg</c> (24) path classes. Regular by
/// default, Filled only for the selected or active state.
/// </summary>
public static class Icons
{
${lines.join('\n')}
}
`.replaceAll('\n', '\r\n'));
if (process.argv[4]) writeFileSync(process.argv[4], JSON.stringify(svgs));
console.log(`wrote ${lines.length} geometries; missing: ${missing.join(', ') || 'none'}`);
