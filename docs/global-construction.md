# Global construction

Open **Empire Management → Global Construction**. The screen is a single building
catalog with an orbital construction background, category filters, search, and a
detail strip. Each row shows completed copies across your empire, copies in
construction, the next recommended colony, and estimated delivery time. The catalog
also includes existing buildings whose technology is not currently unlocked, so
conquered infrastructure still contributes to the totals.

Single-click to inspect a building. **Double-click to immediately queue one copy**
on the best eligible colony, or use **Build on best colony**. There is no draft or
confirmation step. Counts and the recommendation refresh after each successful
enqueue. Consecutive builds are ranked again against the latest real queues; a
unique building can move to the next eligible colony on the next double-click.
The status strip reports the destination or explains that no colony is eligible.

Selection checks unlocked technology, per-colony and empire-wide uniqueness,
terraformer limits, and valid unoccupied tiles. Buildings that permit any terrain
prefer uninhabitable tiles to preserve habitable space. Ranking combines:

- Governor building benefit, including colony needs and local resource traits.
- Matching industrial, research, agricultural or military specialization.
- Production capacity after worker allocation, mineral yield, taxes and consumption.
- Remaining work in the construction queue, including ships and troops.
- Stored production, limited by infrastructure spending per turn.
- Available building slots, favoring spare capacity over the last valid tile.
- Current combat and sabotage, including the remaining sabotage delay.

The ranking score is `(1 + log(1 + benefit)) * space * specialization * safety /
(1 + estimatedTurns / 12)`. Space ranges from 0.84 to 1, specialization adds 20%,
and current combat/sabotage multiplies suitability by 0.35. Non-finite governor
ratios fall back to zero benefit. A stalled colony receives zero score and is used
only when no candidate has a finite delivery estimate.

Delivery is an estimate, not a promise. The forecast uses current colony statistics
and governor worker preferences, accounts for the queue ahead, and spends the
stockpile only once. Future imports, labor changes, completed production bonuses,
rushing, and queue reprioritization can change the actual completion time.

Snapshot creation and queue mutations run on the simulation thread. Each build
rechecks eligibility and reserves its tile through the existing construction API.
Submitted buildings are ordinary player-added queue items: existing cancellation,
governor protection settings, and save serialization continue to apply. Cancel
them from the destination colony's construction screen.

## Art and validation

The background was generated using built-in imagegen and is loaded directly from
`game/Content/Textures/NewUI/GlobalConstruction/background.png`.
Its full prompt is saved in `docs/global-construction-background.prompt.txt`.

Actual UI renders (local test artifacts under the ignored `output/` directory):

- `output/imagegen/empire-global-construction-v2-1920x1080.png`
- `output/imagegen/empire-global-construction-v2-1280x720.png`
- `output/imagegen/empire-global-construction-v2-1024x768.png`

`GlobalConstructionTests` covers counts, foreign-colony exclusion, direct queueing,
repeated copies, uniqueness, tile reservations, stale destinations, cancellation,
production capacity, queue load, slot pressure, sabotage, stockpile limits,
asynchronous double-click dispatch, search, and rendering at these resolutions.
