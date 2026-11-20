# How the readiness score works

One number per destination, 0 to 100, from four parts that are shown beside it. The arithmetic is
deliberately simple enough to explain in a sentence each.

| Part | Role | What it measures | How |
| --- | --- | --- | --- |
| Coverage | multiplies everything | Is there a copy at all? | Files found (verified, outdated or corrupt) ÷ files sampled; a file that is missing, cloud only or unreadable is not found |
| Freshness | 20 of the remaining 40 points | How far behind is the backup? | 1 − lag ÷ 30 days, floored at 0; 1 when nothing is behind |
| Integrity | 10 points | Are the copies the right bytes? | (found − corrupt + re-checked − rotted) ÷ (found + re-checked) |
| Reachability | 10 points | Is the destination usually there? | 0.7 × share of the last 10 drills that reached it + 0.3 × this drill; this drill alone when there is no history |

**Total** = 100 × coverage × (0.60 + 0.20 freshness + 0.10 integrity + 0.10 reachability), rounded.
Coverage comes first because a copy that does not exist cannot be fresh or intact: a backup holding every
sampled file, up to date and intact, and there at every drill, scores 100; one holding 90% of them scores
at most 90; one holding half scores at most 50. For a backup holding every sampled file, a week behind
costs about 5 points (95), two weeks about 9 (91), a month or more 20 (80).

Two things cap the total at 69, so the verdict is "needs attention" at best:

- any corrupt file, or any copy a re-check finds rotted, because a backup with corruption in it must never
  read as one that would restore, fully or mostly;
- a protected folder none of whose sampled files was found here, because that whole folder would be lost
  with the PC, however small its share of the sample.

| Total | Verdict |
| --- | --- |
| 95 to 100 | would restore |
| 70 to 94 | would mostly restore |
| 40 to 69 | needs attention |
| 0 to 39 | would not restore |

Two examples. The backup drive in the README: 400 sampled files, all found and 3 of them older than the
original, the oldest change not yet copied a day old, no corruption, there at every drill. That is
100 × 1 × (0.60 + 0.20 × 29/30 + 0.10 + 0.10) = 99.3, so 99, "would restore". A drive that holds all 200
sampled files of Documents but none of the 10 sampled from the desktop: coverage is 200 ÷ 210 = 95.2%,
which would score 95, but the cap makes it 69, "needs attention".

A destination that could not be reached has no score and reads "could not be reached". A drill that could
sample nothing, because every file was online only, too large or just changed, say, reads "nothing to
check" and says why. A destination that an unforeseen error kept the drill from checking, such as a scratch
disk that filled, has no score and reads "could not be checked", with the error; the other destinations
keep their results, it never counts as a pass, and it is left out of reachability. A protected folder that
is not there is named as not found. No backup's score moves for it, since no backup is at fault, but the
drill says so and does not count as a pass.

## What each file can be

| Outcome | Meaning |
| --- | --- |
| Verified | A copy exists and its SHA-256 matches the original |
| Outdated | A copy exists but was written more than 2 seconds before the original and its bytes differ: the backup has not caught up |
| Corrupt | A copy exists, is not older than the original, yet its bytes differ |
| Missing | No copy where the layout says it should be |
| Placeholder | A cloud placeholder: the name is on the disk, the bytes are not. Never downloaded |
| Error | The copy or the original could not be read. When an rclone remote's list of files cannot be read (an expired sign-in, say), the files looked for there count here too, not as missing |
| Rotted | Found by a re-check, not the sample: a copy verified in an earlier drill now has other bytes under the same name, size and date |

A copy a backup tool reports as damaged as it reads it (restic and Kopia check every piece they restore,
7-Zip every file's CRC) counts as corrupt, not as unreadable.

## Re-checks: the hash ledger

Every copy that reads back correctly is remembered with its hash, its size and its write time, up to 5,000
per destination. Each drill reads up to 25 of them again, within 64 MB, those verified longest ago first
and none verified in the last 20 hours; the original is not needed, because the copy is compared with
itself. The same bytes: the copy counts as re-checked. Other bytes under the same name, size and date: it
has rotted, and it is read again every drill until the backup replaces it. A copy the backup replaced or
removed is simply forgotten. Re-checked copies count towards integrity alongside the sample's; coverage
and freshness come from the sample alone. For restic and Kopia, whose own checks would have refused damaged
bytes, other bytes that pass are a new version, not rot.

## Lag

Freshness looks only at changes newer than anything verified, and each protected folder is judged on its
own, so one folder's fresh copies cannot hide another's stalled backup. In each folder, the oldest sampled
file that is missing, outdated or a placeholder *and* was written after the newest verified file of that
folder marks when the folder fell behind; "behind since" is the earliest of these, and lag is the time
since then. An old file that was never copied is a coverage hole, not a lag, so it does not count here,
and a folder with nothing verified has no lag to measure. If nothing is behind, freshness is 1.

With canary files on, each protected folder holds "Boatdrill canary.txt", stamped after every drill. The
oldest stamp a backup's copy lacks, once it is at least a day old, is when that backup fell behind, and
counts like any other change: the earliest of them all is used. That is what shows a backup that stopped
while nothing else in the folder changed.

## Margin of error

Coverage is estimated from a sample of *n* files out of the *N* in the sources. The half-width of the 95%
interval is 1.96 × √(p(1 − p) ÷ n × (N − n) ÷ (N − 1)); 200 files out of 10,000 at 95% coverage gives about
±3%. The app and the report show it beside the coverage figure when it is half a percent or more, and
boatdrill-cli always prints it; the sample size is a setting. When every sampled file has a copy, the
formula gives no margin at all, which does not mean that every file outside the sample has one too.

That formula is for a simple random sample, and Boatdrill's is not one: recent files are drawn more often,
each top-level folder adds a couple of its own, and the largest files are the first trimmed to fit the byte
cap. So coverage is the share of the sample that has a copy, leaning towards recent files, and the margin
is a rough guide: a backup that misses mostly old files, such as an old folder never added to it, holds a
smaller share of all files than coverage says, and one that misses mostly recent files a larger one.

## Full-restore estimate

Bytes in all sources, online-only files left out, ÷ the read-back speed this drill measured on this
destination. It is an estimate of reading, not of writing to a replacement disk, and it is shown only when
something was read.

## Sampling

One pass over each source with weighted reservoir sampling: every file gets the key u^(1/w) for a uniform
random u and a weight w of 4 (changed in the last 7 days), 2 (30 days), 1 (a year) or 0.5 (older); the
files with the largest keys are the sample, 200 per source by default. Each top-level folder also keeps a
reservoir of 2, whose files join the sample, so a folder is not left out by the luck of the draw; the byte
cap can still trim them, and a folder that holds only files left out below gives none. Files written in
the last 2 hours are left out (they may still be changing), files over 256 MB are left out (one could eat
the whole byte cap), and the sample is trimmed largest-first to the byte cap, 512 MB per source by
default, always keeping at least one file. Online-only files, whose bytes are in the cloud (OneDrive's
Files On-Demand, say), are left out and counted, never downloaded. Hidden and system files, links, the
names a folder is set to leave out (the app leaves out `Thumbs.db`, `*.tmp` and the like) and the canary
file are neither sampled nor counted.
