# Simulation Report

- Scenarios: **16**
- Per-scenario duration: **1200 game-sec** (20.0 min)
- Sample period: **30 game-sec**
- Map size: **400×400** (1/4 of live game's 800×800)
- Sim dt: **0.05 sec**

## Summary table (final values)

| Scenario | FF peak | FF final | FF mean | FF min | FF died hunger | Worm peak | Worm final | Worm kills | Worm dug | Cryst mature | Rock % | Moss % | Mushrooms |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 00_baseline | 50 | 4 | 11.4 | 3 | 64 | 6 | 1 | 2 | 0 | 100 | 70.2 | 3.0 | 500 |
| gen_dense_rock | 50 | 7 | 11.9 | 4 | 69 | 6 | 2 | 3 | 0 | 100 | 85.9 | 2.2 | 500 |
| gen_sparse_rock | 50 | 7 | 12.0 | 4 | 63 | 6 | 2 | 1 | 0 | 100 | 48.9 | 5.0 | 500 |
| gen_more_perlin | 50 | 9 | 12.0 | 3 | 51 | 6 | 2 | 4 | 0 | 100 | 61.4 | 3.8 | 500 |
| gen_no_perlin | 51 | 6 | 7.0 | 0 | 78 | 6 | 2 | 0 | 0 | 39 | 83.6 | 1.8 | 500 |
| ff_hunger_low | 55 | 8 | 13.8 | 4 | 63 | 6 | 1 | 4 | 0 | 100 | 74.4 | 3.4 | 500 |
| ff_hunger_high | 50 | 4 | 7.3 | 2 | 88 | 6 | 2 | 2 | 0 | 87 | 75.5 | 2.4 | 500 |
| ff_short_life | 71 | 6 | 14.0 | 3 | 83 | 6 | 0 | 3 | 0 | 100 | 71.3 | 3.8 | 500 |
| ff_long_life | 50 | 5 | 8.9 | 1 | 83 | 6 | 2 | 0 | 0 | 91 | 77.1 | 2.3 | 500 |
| ff_more_breed | 53 | 11 | 14.7 | 5 | 72 | 6 | 2 | 10 | 0 | 100 | 70.5 | 3.6 | 500 |
| worm_hunger_low | 50 | 7 | 10.1 | 3 | 60 | 6 | 2 | 8 | 0 | 100 | 73.3 | 2.8 | 500 |
| worm_hunger_high | 72 | 5 | 16.6 | 3 | 118 | 6 | 2 | 4 | 0 | 100 | 69.0 | 4.0 | 500 |
| worm_hunt_short | 52 | 7 | 9.9 | 4 | 77 | 6 | 1 | 2 | 0 | 100 | 73.8 | 2.7 | 500 |
| worm_hunt_long | 50 | 7 | 13.0 | 4 | 70 | 6 | 2 | 3 | 0 | 100 | 71.8 | 3.5 | 500 |
| crystal_slow | 53 | 6 | 12.9 | 3 | 89 | 6 | 2 | 2 | 0 | 71 | 71.6 | 3.2 | 500 |
| crystal_fast | 50 | 2 | 9.6 | 2 | 85 | 6 | 0 | 3 | 0 | 100 | 78.5 | 2.4 | 500 |

## Per-scenario detail

### 00_baseline

**Population dynamics**

- Fireflies: peak **50**, final **4**, mean 11.4, range 3…50
- Worms: peak **6**, final **1**, mean 1.9

**Lifetime totals**

- FF born: init 50, breed 16, replenish 54, moss 1
- FF died: age 51, hunger 64, predator 2
- Worm born: init 6, breed 0, replenish 23
- Worm died: age 0, hunger 28
- Worm interactions: kills **2**, cells dug **0**
- Crystals: growing 0, mature 100, seeded **100**, destroyed 0
- Map: rocks 70.2%, moss 3.0%, mushrooms 500

### gen_dense_rock

Overrides:
- `Rocks.NoiseThreshold` = `0.32`

**Population dynamics**

- Fireflies: peak **50**, final **7**, mean 11.9, range 4…50
- Worms: peak **6**, final **2**, mean 2.0

**Lifetime totals**

- FF born: init 50, breed 23, replenish 49, moss 0
- FF died: age 43, hunger 69, predator 3
- Worm born: init 6, breed 0, replenish 22
- Worm died: age 0, hunger 26
- Worm interactions: kills **3**, cells dug **0**
- Crystals: growing 0, mature 100, seeded **100**, destroyed 0
- Map: rocks 85.9%, moss 2.2%, mushrooms 500

### gen_sparse_rock

Overrides:
- `Rocks.NoiseThreshold` = `0.48`

**Population dynamics**

- Fireflies: peak **50**, final **7**, mean 12.0, range 4…50
- Worms: peak **6**, final **2**, mean 2.0

**Lifetime totals**

- FF born: init 50, breed 5, replenish 67, moss 1
- FF died: age 52, hunger 63, predator 1
- Worm born: init 6, breed 0, replenish 24
- Worm died: age 0, hunger 28
- Worm interactions: kills **1**, cells dug **0**
- Crystals: growing 0, mature 100, seeded **100**, destroyed 0
- Map: rocks 48.9%, moss 5.0%, mushrooms 500

### gen_more_perlin

Overrides:
- `Rocks.PerlinCaveDensity` = `0.25`

**Population dynamics**

- Fireflies: peak **50**, final **9**, mean 12.0, range 3…50
- Worms: peak **6**, final **2**, mean 1.9

**Lifetime totals**

- FF born: init 50, breed 3, replenish 64, moss 1
- FF died: age 54, hunger 51, predator 4
- Worm born: init 6, breed 0, replenish 22
- Worm died: age 0, hunger 26
- Worm interactions: kills **4**, cells dug **0**
- Crystals: growing 0, mature 100, seeded **100**, destroyed 0
- Map: rocks 61.4%, moss 3.8%, mushrooms 500

### gen_no_perlin

Overrides:
- `Rocks.PerlinCaveDensity` = `0`

**Population dynamics**

- Fireflies: peak **51**, final **6**, mean 7.0, range 0…50
- Worms: peak **6**, final **2**, mean 1.9

**Lifetime totals**

- FF born: init 50, breed 3, replenish 39, moss 0
- FF died: age 8, hunger 78, predator 0
- Worm born: init 6, breed 0, replenish 25
- Worm died: age 0, hunger 29
- Worm interactions: kills **0**, cells dug **0**
- Crystals: growing 0, mature 39, seeded **39**, destroyed 0
- Map: rocks 83.6%, moss 1.8%, mushrooms 500

### ff_hunger_low

Overrides:
- `Fireflies.HungerDecay` = `1.2`

**Population dynamics**

- Fireflies: peak **55**, final **8**, mean 13.8, range 4…52
- Worms: peak **6**, final **1**, mean 2.0

**Lifetime totals**

- FF born: init 50, breed 25, replenish 54, moss 0
- FF died: age 54, hunger 63, predator 4
- Worm born: init 6, breed 0, replenish 22
- Worm died: age 0, hunger 27
- Worm interactions: kills **4**, cells dug **0**
- Crystals: growing 0, mature 100, seeded **100**, destroyed 0
- Map: rocks 74.4%, moss 3.4%, mushrooms 500

### ff_hunger_high

Overrides:
- `Fireflies.HungerDecay` = `2.4`

**Population dynamics**

- Fireflies: peak **50**, final **4**, mean 7.3, range 2…49
- Worms: peak **6**, final **2**, mean 1.9

**Lifetime totals**

- FF born: init 50, breed 7, replenish 50, moss 0
- FF died: age 13, hunger 88, predator 2
- Worm born: init 6, breed 0, replenish 23
- Worm died: age 0, hunger 27
- Worm interactions: kills **2**, cells dug **0**
- Crystals: growing 3, mature 87, seeded **90**, destroyed 0
- Map: rocks 75.5%, moss 2.4%, mushrooms 500

### ff_short_life

Overrides:
- `Fireflies.BaseMaxAge` = `120`

**Population dynamics**

- Fireflies: peak **71**, final **6**, mean 14.0, range 3…65
- Worms: peak **6**, final **0**, mean 1.9

**Lifetime totals**

- FF born: init 50, breed 85, replenish 55, moss 0
- FF died: age 98, hunger 83, predator 3
- Worm born: init 6, breed 0, replenish 22
- Worm died: age 1, hunger 27
- Worm interactions: kills **3**, cells dug **0**
- Crystals: growing 0, mature 100, seeded **100**, destroyed 0
- Map: rocks 71.3%, moss 3.8%, mushrooms 500

### ff_long_life

Overrides:
- `Fireflies.BaseMaxAge` = `240`

**Population dynamics**

- Fireflies: peak **50**, final **5**, mean 8.9, range 1…50
- Worms: peak **6**, final **2**, mean 1.9

**Lifetime totals**

- FF born: init 50, breed 10, replenish 38, moss 0
- FF died: age 10, hunger 83, predator 0
- Worm born: init 6, breed 0, replenish 24
- Worm died: age 0, hunger 28
- Worm interactions: kills **0**, cells dug **0**
- Crystals: growing 0, mature 91, seeded **91**, destroyed 0
- Map: rocks 77.1%, moss 2.3%, mushrooms 500

### ff_more_breed

Overrides:
- `Fireflies.BreedChancePerTick` = `0.1`

**Population dynamics**

- Fireflies: peak **53**, final **11**, mean 14.7, range 5…49
- Worms: peak **6**, final **2**, mean 2.0

**Lifetime totals**

- FF born: init 50, breed 44, replenish 60, moss 0
- FF died: age 61, hunger 72, predator 10
- Worm born: init 6, breed 0, replenish 20
- Worm died: age 2, hunger 22
- Worm interactions: kills **10**, cells dug **0**
- Crystals: growing 0, mature 100, seeded **100**, destroyed 0
- Map: rocks 70.5%, moss 3.6%, mushrooms 500

### worm_hunger_low

Overrides:
- `Worms.HungerDecay` = `0.5`

**Population dynamics**

- Fireflies: peak **50**, final **7**, mean 10.1, range 3…49
- Worms: peak **6**, final **2**, mean 2.2

**Lifetime totals**

- FF born: init 50, breed 8, replenish 51, moss 0
- FF died: age 34, hunger 60, predator 8
- Worm born: init 6, breed 0, replenish 13
- Worm died: age 9, hunger 8
- Worm interactions: kills **8**, cells dug **0**
- Crystals: growing 0, mature 100, seeded **100**, destroyed 0
- Map: rocks 73.3%, moss 2.8%, mushrooms 500

### worm_hunger_high

Overrides:
- `Worms.HungerDecay` = `1.5`

**Population dynamics**

- Fireflies: peak **72**, final **5**, mean 16.6, range 3…72
- Worms: peak **6**, final **2**, mean 1.6

**Lifetime totals**

- FF born: init 50, breed 75, replenish 55, moss 0
- FF died: age 53, hunger 118, predator 4
- Worm born: init 6, breed 0, replenish 33
- Worm died: age 0, hunger 37
- Worm interactions: kills **4**, cells dug **0**
- Crystals: growing 0, mature 100, seeded **100**, destroyed 0
- Map: rocks 69.0%, moss 4.0%, mushrooms 500

### worm_hunt_short

Overrides:
- `Worms.HuntRadius` = `300`

**Population dynamics**

- Fireflies: peak **52**, final **7**, mean 9.9, range 4…50
- Worms: peak **6**, final **1**, mean 2.0

**Lifetime totals**

- FF born: init 50, breed 16, replenish 53, moss 0
- FF died: age 33, hunger 77, predator 2
- Worm born: init 6, breed 0, replenish 23
- Worm died: age 0, hunger 28
- Worm interactions: kills **2**, cells dug **0**
- Crystals: growing 0, mature 100, seeded **100**, destroyed 0
- Map: rocks 73.8%, moss 2.7%, mushrooms 500

### worm_hunt_long

Overrides:
- `Worms.HuntRadius` = `900`

**Population dynamics**

- Fireflies: peak **50**, final **7**, mean 13.0, range 4…50
- Worms: peak **6**, final **2**, mean 1.9

**Lifetime totals**

- FF born: init 50, breed 28, replenish 55, moss 0
- FF died: age 53, hunger 70, predator 3
- Worm born: init 6, breed 0, replenish 23
- Worm died: age 0, hunger 27
- Worm interactions: kills **3**, cells dug **0**
- Crystals: growing 0, mature 100, seeded **100**, destroyed 0
- Map: rocks 71.8%, moss 3.5%, mushrooms 500

### crystal_slow

Overrides:
- `Fireflies.FeedingsBeforeCrystal` = `50`

**Population dynamics**

- Fireflies: peak **53**, final **6**, mean 12.9, range 3…53
- Worms: peak **6**, final **2**, mean 2.0

**Lifetime totals**

- FF born: init 50, breed 34, replenish 54, moss 0
- FF died: age 41, hunger 89, predator 2
- Worm born: init 6, breed 0, replenish 23
- Worm died: age 0, hunger 27
- Worm interactions: kills **2**, cells dug **0**
- Crystals: growing 1, mature 71, seeded **72**, destroyed 0
- Map: rocks 71.6%, moss 3.2%, mushrooms 500

### crystal_fast

Overrides:
- `Fireflies.FeedingsBeforeCrystal` = `15`

**Population dynamics**

- Fireflies: peak **50**, final **2**, mean 9.6, range 2…49
- Worms: peak **6**, final **0**, mean 1.9

**Lifetime totals**

- FF born: init 50, breed 23, replenish 45, moss 0
- FF died: age 28, hunger 85, predator 3
- Worm born: init 6, breed 0, replenish 22
- Worm died: age 0, hunger 28
- Worm interactions: kills **3**, cells dug **0**
- Crystals: growing 0, mature 100, seeded **100**, destroyed 0
- Map: rocks 78.5%, moss 2.4%, mushrooms 500
