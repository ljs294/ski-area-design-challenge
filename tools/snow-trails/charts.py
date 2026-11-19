# Demo 113 (task 4.07 B1 part 2a): charts from TS90's CSVs. Usage: python charts.py <test-results/snow-b2a>
import csv, os, sys
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

root = sys.argv[1]
out = os.path.join(root, 'demo'); os.makedirs(out, exist_ok=True)
BLUE, ORANGE, INK, MUTED, GRID = '#2a78d6', '#eb6834', '#1f1f1d', '#6b6a63', '#e4e3dd'
plt.rcParams.update({'font.size': 15, 'axes.edgecolor': MUTED, 'axes.labelcolor': INK, 'xtick.color': MUTED, 'ytick.color': MUTED,
                     'axes.spines.top': False, 'axes.spines.right': False})

# 1. Through a thaw: water kept, groomed vs loose.
rows = list(csv.DictReader(open(os.path.join(root, 'thaw.csv'))))
days = [int(r['hour']) / 24 for r in rows]
g = [float(r['groomed_mm']) / 25.4 for r in rows]; l = [float(r['loose_mm']) / 25.4 for r in rows]
fig, ax = plt.subplots(figsize=(10.8, 7.2), dpi=100)
ax.plot(days, g, color=BLUE, lw=2.5); ax.plot(days, l, color=ORANGE, lw=2.5)
ax.text(days[-1] + 0.08, g[-1] + 0.15, f'Groomed nightly  {g[-1]:.1f} in', color=INK, va='bottom', fontsize=14)
ax.text(days[-1] + 0.08, l[-1] - 0.15, f'Never groomed  {l[-1]:.1f} in', color=INK, va='top', fontsize=14)
ax.set_xlim(0, 7.6); ax.set_xlabel('Days of a warm spell'); ax.set_ylabel('Water in the snow (inches)')
ax.grid(axis='y', color=GRID); ax.set_axisbelow(True)
ax.legend(['Groomed nightly', 'Never groomed'], frameon=False, loc='upper right')
fig.suptitle('The same run, groomed or not, through a thaw', x=0.06, ha='left', fontsize=20, color=INK)
fig.text(0.06, 0.875, f'Both start with 59 in of snow. Packed snow melts slower, so the groomed run\nkeeps '
         f'{(g[-1] / l[-1] - 1) * 100:.0f}% more water after 6 days.', color=MUTED, fontsize=13)
fig.text(0.06, 0.02, 'Drawn from the simulation\'s numbers (test TS90), not a game screenshot.', color=MUTED, fontsize=12)
fig.subplots_adjust(top=0.8, right=0.78, bottom=0.14)
fig.savefig(os.path.join(out, '1-thaw.png')); plt.close(fig)

# 2. Across the run before and after one night's grooming: depth by lane, one band of the run.
rows = list(csv.DictReader(open(os.path.join(root, 'groom.csv'))))
alongs = sorted({int(r['along_mm']) for r in rows})
def by(key):
    d = {}
    for r in rows: d.setdefault(int(r['along_mm']), []).append(float(r[key]) / 25.4)
    return d
before, after = by('before_mm'), by('after_mm')
xs = [a / 304.8 for a in alongs]
def spread(d): return [max(d[a]) - min(d[a]) for a in alongs]
def mean(d): return [sum(d[a]) / len(d[a]) for a in alongs]
fig, (a1, a2) = plt.subplots(2, 1, figsize=(10.8, 9.0), dpi=100, sharex=True)
a1.plot(xs, mean(before), color=ORANGE, lw=2.2); a1.plot(xs, mean(after), color=BLUE, lw=2.2)
a1.set_ylabel('Mean depth (in)'); a1.grid(axis='y', color=GRID); a1.set_axisbelow(True)
a1.legend(['Before the night', 'After grooming'], frameon=False, loc='lower left')
a1.set_title('Depth along the run: packed down, top still deeper than the bottom', loc='left', fontsize=15, color=INK)
a2.plot(xs, spread(before), color=ORANGE, lw=2.2); a2.plot(xs, spread(after), color=BLUE, lw=2.2)
a2.set_ylabel('Deepest minus shallowest\nacross the width (in)'); a2.set_xlabel('Feet down the run from the top')
a2.grid(axis='y', color=GRID); a2.set_axisbelow(True)
a2.set_title('Across the run: the two sides end the night closer to even', loc='left', fontsize=15, color=INK)
fig.suptitle('One night of grooming on a run after two warm days', x=0.06, ha='left', fontsize=20, color=INK)
fig.text(0.06, 0.02, 'Drawn from the simulation\'s numbers (test TS90), not a game screenshot. The water on the run is the same before and after.',
         color=MUTED, fontsize=11)
fig.subplots_adjust(top=0.9, bottom=0.1, hspace=0.3)
fig.savefig(os.path.join(out, '2-one-night.png')); plt.close(fig)
mb, ma = mean(before), mean(after); sb, sa = spread(before), spread(after)
print('depth top/bottom before %.1f/%.1f after %.1f/%.1f; mean spread before %.2f after %.2f' %
      (mb[0], mb[-1], ma[0], ma[-1], sum(sb) / len(sb), sum(sa) / len(sa)))
