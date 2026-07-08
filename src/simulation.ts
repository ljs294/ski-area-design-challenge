import type { GameState, ResortNode, Skier, SkierSkill } from './types';

/**
 * Breadth-First Search pathfinder to locate a route of edges (Lifts/Trails) 
 * from a start node to a node matching a target condition (e.g. type === 'lodge').
 */
export function findPath(
  startNodeId: string,
  targetPredicate: (node: ResortNode) => boolean,
  state: GameState,
  skill: SkierSkill
): string[] | null { // Returns list of edge IDs (Lifts or Trails)
  const queue: { nodeId: string; path: string[] }[] = [{ nodeId: startNodeId, path: [] }];
  const visited = new Set<string>([startNodeId]);

  while (queue.length > 0) {
    const { nodeId, path } = queue.shift()!;
    const currentNode = state.nodes.find(n => n.id === nodeId);
    if (!currentNode) continue;

    // Check if target is met
    if (targetPredicate(currentNode)) {
      return path;
    }

    // Find outgoing edges
    // 1. Trails (must go down, match skill level)
    const outgoingTrails = state.trails.filter(t => {
      if (t.sourceNodeId !== nodeId) return false;
      
      // Skill checks: beginner cannot ski black/double_black, intermediate cannot ski double_black
      if (skill === 'beginner' && (t.difficulty === 'black' || t.difficulty === 'double_black')) return false;
      if (skill === 'intermediate' && t.difficulty === 'double_black') return false;
      
      // Snow checks (must have some snow)
      if (t.snowDepth <= 0) return false;

      return true;
    });

    for (const trail of outgoingTrails) {
      if (!visited.has(trail.targetNodeId)) {
        visited.add(trail.targetNodeId);
        queue.push({ nodeId: trail.targetNodeId, path: [...path, trail.id] });
      }
    }

    // 2. Lifts (must go up, must be open)
    const outgoingLifts = state.lifts.filter(l => l.sourceNodeId === nodeId && !l.isClosed);
    for (const lift of outgoingLifts) {
      if (!visited.has(lift.targetNodeId)) {
        visited.add(lift.targetNodeId);
        queue.push({ nodeId: lift.targetNodeId, path: [...path, lift.id] });
      }
    }

    // 3. Bi-directional Lifts (can download)
    const downloadingLifts = state.lifts.filter(l => l.targetNodeId === nodeId && l.isBidirectional && !l.isClosed);
    for (const lift of downloadingLifts) {
      if (!visited.has(lift.sourceNodeId)) {
        visited.add(lift.sourceNodeId);
        queue.push({ nodeId: lift.sourceNodeId, path: [...path, lift.id] });
      }
    }
  }

  return null; // No path found
}

/**
 * Core simulation runner
 */
export class SimulationEngine {
  
  public static tick(state: GameState): string[] {
    const alerts: string[] = [];
    
    if (state.timeSpeed === 0 || !state.terrain) {
      return alerts; // Paused or no map
    }

    // 1. Advance Game Time
    // Clock runs 8:00 AM (480 mins) to 5:00 PM (1020 mins)
    const minutesPerTick = 0.5 * state.timeSpeed;
    state.gameTimeMinutes += minutesPerTick;

    // Check for end of day
    if (state.gameTimeMinutes >= 1020) {
      this.handleEndOfDay(state, alerts);
      return alerts;
    }

    // 2. Weather Dynamics
    this.updateWeather(state);
    
    // Check for lift shutdowns due to high winds
    for (const lift of state.lifts) {
      const wasClosed = lift.isClosed;
      if (state.windSpeed > lift.maxWindSpeed) {
        lift.isClosed = true;
        if (!wasClosed) {
          alerts.push(`⚠️ ${lift.name} shut down due to high wind (${Math.round(state.windSpeed)} km/h)!`);
        }
      } else {
        lift.isClosed = false;
        if (wasClosed) {
          alerts.push(`✅ ${lift.name} reopened as wind calmed.`);
        }
      }
    }

    // 3. Spawn Skiers
    this.spawnSkiers(state);

    // 4. Update Lifts
    this.updateLifts(state);

    // 5. Update Trails
    this.updateTrails(state);

    // 6. Update Skier Agents
    this.updateSkiers(state, alerts);

    return alerts;
  }

  private static updateWeather(state: GameState): void {
    if (!state.terrain) return;
    const currentMonthClimate = state.terrain.climate.monthly[state.gameMonth];
    
    // Calculate a daily diurnal temperature curve peaking at 2:00 PM (840 mins)
    const timeRatio = (state.gameTimeMinutes - 480) / (1020 - 480); // 0 to 1
    const tempRange = currentMonthClimate.tempHigh - currentMonthClimate.tempLow;
    // Sinusoidal temperature curve
    const seasonalSwing = Math.sin((timeRatio * Math.PI) - (Math.PI / 4)); // peaks in afternoon
    state.temperature = Math.round(currentMonthClimate.tempLow + (tempRange * (seasonalSwing + 1) / 2));
    
    // Procedural wind spikes
    if (Math.random() < 0.01) {
      // Set new target wind speed around monthly average +/- 25 km/h
      const targetWind = Math.max(0, currentMonthClimate.avgWindSpeed + (Math.random() * 50 - 25));
      state.windSpeed = state.windSpeed * 0.9 + targetWind * 0.1; // smooth interpolation
    }

    // Snow probability check
    if (state.temperature < 32 && Math.random() < 0.0005) {
      state.isSnowing = Math.random() < currentMonthClimate.snowProbability;
    } else if (state.temperature >= 35) {
      state.isSnowing = false;
    }
  }

  private static spawnSkiers(state: GameState): void {
    // Only spawn during peak morning hours (8:30 AM to 11:30 AM)
    if (state.gameTimeMinutes < 510 || state.gameTimeMinutes > 690) {
      return;
    }

    const entranceNode = state.nodes.find(n => n.type === 'entrance');
    if (!entranceNode) return;

    // Spawning rate depends on ticket price, weather, and game time
    // Base rate: 1 skier every few ticks
    const weatherPenalty = state.isSnowing ? 0.7 : (state.temperature < 15 ? 0.8 : 1.0);
    const priceFactor = Math.max(0.1, 1.0 - (state.ticketPrice - 40) / 100); // ticket price demand curve
    const spawnChance = 0.15 * weatherPenalty * priceFactor * state.timeSpeed;
    
    if (Math.random() < spawnChance) {
      const skills: SkierSkill[] = ['beginner', 'intermediate', 'expert'];
      const skill = skills[Math.floor(Math.random() * skills.length)];
      
      const newSkier: Skier = {
        id: `skier-${Math.random().toString(36).substr(2, 9)}`,
        skill,
        energy: 80 + Math.floor(Math.random() * 20),
        satisfaction: 100,
        cash: 60 + Math.floor(Math.random() * 150),
        state: 'spawning',
        currentNodeId: entranceNode.id,
        currentEdgeId: '',
        currentEdgeProgress: 0,
        routeQueue: [],
        lastActivityTime: state.gameTimeMinutes,
        recentTrails: [],
        x: entranceNode.x,
        y: entranceNode.y,
        z: entranceNode.z
      };

      // Charge ticket price
      newSkier.cash -= state.ticketPrice;
      state.ledger.revenue += state.ticketPrice;
      state.cash += state.ticketPrice;

      state.skiers.push(newSkier);
    }
  }

  private static updateLifts(state: GameState): void {
    const chairSpeedPixelsPerTick = 1.8 * state.timeSpeed; // standard lift speed

    for (const lift of state.lifts) {
      const startNode = state.nodes.find(n => n.id === lift.sourceNodeId);
      const endNode = state.nodes.find(n => n.id === lift.targetNodeId);
      if (!startNode || !endNode) continue;

      // 1. Advance active chairs along the cable
      for (const chair of lift.chairs) {
        chair.progress += (chairSpeedPixelsPerTick / lift.capacity) * 0.05; // speed scales with lift length capacity
        
        // Check for chair arrival at top terminal
        if (chair.progress >= 1.0) {
          chair.progress = 0.0; // recycle chair to bottom
          
          // Discharge passengers
          for (const skierId of chair.passengers) {
            const skier = state.skiers.find(s => s.id === skierId);
            if (skier) {
              skier.state = 'deciding';
              skier.currentNodeId = lift.targetNodeId;
              skier.currentEdgeId = '';
              skier.x = endNode.x;
              skier.y = endNode.y;
              skier.z = endNode.z;
            }
          }
          chair.passengers = [];
        }
      }

      // 2. Board skiers from queue if lift is open
      if (!lift.isClosed && lift.queue.length > 0) {
        // Find empty/partially full chairs near bottom (progress < 0.05)
        const boardingChair = lift.chairs.find(c => c.progress < 0.08 && c.passengers.length < lift.capacity);
        if (boardingChair) {
          const spotsAvailable = lift.capacity - boardingChair.passengers.length;
          const boardCount = Math.min(spotsAvailable, lift.queue.length);
          
          for (let i = 0; i < boardCount; i++) {
            const skierId = lift.queue.shift()!;
            boardingChair.passengers.push(skierId);
            
            const skier = state.skiers.find(s => s.id === skierId);
            if (skier) {
              skier.state = 'riding';
              skier.currentEdgeId = lift.id;
              skier.currentEdgeProgress = boardingChair.progress;
            }
          }
        }
      }
    }
  }

  private static updateTrails(state: GameState): void {
    const skierSpeedPixels = 2.5 * state.timeSpeed;

    for (const trail of state.trails) {
      // Natural snowfall accumulation
      if (state.isSnowing) {
        trail.snowDepth += 0.01 * state.timeSpeed;
      }
      
      // Melting due to warmth (above 32°F)
      if (state.temperature > 32) {
        const meltRate = (state.temperature - 32) * 0.0005 * state.timeSpeed;
        trail.snowDepth = Math.max(0, trail.snowDepth - meltRate);
      }

      // Dynamic wear and tear: skiers scrap snow down to ice
      const skiersOnTrailCount = trail.skierDensities.length;
      if (skiersOnTrailCount > 0) {
        const wearRate = skiersOnTrailCount * 0.001 * state.timeSpeed;
        trail.snowDepth = Math.max(0, trail.snowDepth - wearRate);
        
        // Congestion reduces grooming quality
        trail.groomingQuality = Math.max(0, trail.groomingQuality - (skiersOnTrailCount * 0.0002));
      }

      // Advance skiers along the trail vector line
      const remainingSkiers: { skierId: string; progress: number }[] = [];
      for (const entry of trail.skierDensities) {
        const skier = state.skiers.find(s => s.id === entry.skierId);
        if (!skier) continue;

        // Skier speed factor based on slope grade, snow quality, and crowding
        const crowdPenalty = Math.max(0.4, 1.0 - (skiersOnTrailCount / 30));
        const snowPenalty = trail.snowDepth < 5 ? 0.5 : (trail.groomingQuality > 0.6 ? 1.1 : 0.9);
        const skierProgressSpeed = (skierSpeedPixels / trail.length) * crowdPenalty * snowPenalty;
        
        entry.progress += skierProgressSpeed;
        skier.currentEdgeProgress = entry.progress;

        // Position interpolation in 2D space along trail points
        const pointIndex = Math.min(
          trail.points.length - 1,
          Math.floor(entry.progress * (trail.points.length - 1))
        );
        const p = trail.points[pointIndex];
        skier.x = p.x;
        skier.y = p.y;
        skier.z = p.z;

        if (entry.progress >= 1.0) {
          // Reached bottom of trail!
          skier.state = 'deciding';
          skier.currentNodeId = trail.targetNodeId;
          skier.currentEdgeId = '';
          
          const endNode = state.nodes.find(n => n.id === trail.targetNodeId);
          if (endNode) {
            skier.x = endNode.x;
            skier.y = endNode.y;
            skier.z = endNode.z;
          }
          
          // Log trail to recent memory to encourage variety
          skier.recentTrails.push(trail.id);
          if (skier.recentTrails.length > 4) skier.recentTrails.shift();

          // Skier energy expenditure
          skier.energy = Math.max(0, skier.energy - (10 + Math.random() * 8));
        } else {
          remainingSkiers.push(entry);
        }
      }
      trail.skierDensities = remainingSkiers;
    }
  }

  private static updateSkiers(state: GameState, alerts: string[]): void {
    const activeSkiers: Skier[] = [];

    for (const skier of state.skiers) {
      
      // Deplete warmth/energy over time
      skier.energy = Math.max(0, skier.energy - 0.02 * state.timeSpeed);
      
      // Low energy reduces satisfaction
      if (skier.energy < 20) {
        skier.satisfaction = Math.max(10, skier.satisfaction - 0.1 * state.timeSpeed);
      }

      if (skier.state === 'spawning' || skier.state === 'deciding') {
        const node = state.nodes.find(n => n.id === skier.currentNodeId);
        if (!node) continue;

        // Check if day is wrapping up (after 4:15 PM / 975 mins)
        const isDayEnding = state.gameTimeMinutes > 975;
        
        if (isDayEnding || skier.cash <= 10) {
          // Head to exit!
          skier.state = 'leaving';
          if (node.type === 'entrance') {
            continue; // despawn, skip adding to activeSkiers list
          }
          
          const path = findPath(skier.currentNodeId, n => n.type === 'entrance', state, skier.skill);
          if (path && path.length > 0) {
            skier.routeQueue = path;
            this.followRoute(skier, state);
          } else {
            // Cannot find path to exit, teleport home
            continue; // despawn
          }
        }
        // Check if tired and needs lodge
        else if (skier.energy < 20) {
          const lodgeNode = state.nodes.find(n => n.type === 'lodge');
          if (lodgeNode) {
            if (skier.currentNodeId === lodgeNode.id) {
              // Arrived at lodge! Enter rest state
              skier.state = 'resting';
              skier.lastActivityTime = state.gameTimeMinutes;
            } else {
              // Pathfind to lodge
              const path = findPath(skier.currentNodeId, n => n.type === 'lodge', state, skier.skill);
              if (path && path.length > 0) {
                skier.routeQueue = path;
                this.followRoute(skier, state);
              } else {
                // If no lodge path, keep skiing but satisfaction drains
                this.chooseRandomAction(skier, state);
              }
            }
          } else {
            // No lodge built, just keep skiing
            this.chooseRandomAction(skier, state);
          }
        } 
        else {
          // Regular decision making
          if (skier.routeQueue.length > 0) {
            this.followRoute(skier, state);
          } else {
            this.chooseRandomAction(skier, state);
          }
        }
      }
      else if (skier.state === 'resting') {
        // Rest in lodge: recovers energy, spends money
        skier.energy = Math.min(100, skier.energy + 1.2 * state.timeSpeed);
        
        // Spend money on snack
        if (state.gameTimeMinutes - skier.lastActivityTime > 30) {
          const snackCost = Math.min(skier.cash, 12);
          skier.cash -= snackCost;
          state.ledger.revenue += snackCost;
          state.cash += snackCost;
          skier.lastActivityTime = state.gameTimeMinutes;
        }

        if (skier.energy >= 95) {
          skier.state = 'deciding';
          skier.satisfaction = Math.min(100, skier.satisfaction + 15);
        }
      }

      // Check for random skier accidents on icy or black trails
      if (skier.state === 'skiing') {
        const trail = state.trails.find(t => t.id === skier.currentEdgeId);
        if (trail) {
          const crashChance = 0.00003 * (2.0 - trail.groomingQuality) * (trail.maxSlope / 25) * state.timeSpeed;
          if (Math.random() < crashChance) {
            alerts.push(`🚨 Accident! A skier crashed on ${trail.name}! Ski Patrol dispatched.`);
            skier.satisfaction = Math.max(10, skier.satisfaction - 40);
            skier.energy = 5; // immobilize
            skier.state = 'resting'; // stay in place resting
            skier.lastActivityTime = state.gameTimeMinutes;
          }
        }
      }

      activeSkiers.push(skier);
    }
    state.skiers = activeSkiers;
  }

  private static followRoute(skier: Skier, state: GameState): void {
    const nextEdgeId = skier.routeQueue.shift()!;
    
    // Is it a lift?
    const lift = state.lifts.find(l => l.id === nextEdgeId);
    if (lift) {
      skier.state = 'queuing';
      skier.currentEdgeId = lift.id;
      lift.queue.push(skier.id);
      return;
    }
    
    // Is it a trail?
    const trail = state.trails.find(t => t.id === nextEdgeId);
    if (trail) {
      skier.state = 'skiing';
      skier.currentEdgeId = trail.id;
      skier.currentEdgeProgress = 0;
      trail.skierDensities.push({ skierId: skier.id, progress: 0 });
      return;
    }
  }

  private static chooseRandomAction(skier: Skier, state: GameState): void {
    // 1. Look for available trails from current node
    const validTrails = state.trails.filter(t => {
      if (t.sourceNodeId !== skier.currentNodeId) return false;
      
      // Skill checks
      if (skier.skill === 'beginner' && (t.difficulty === 'black' || t.difficulty === 'double_black')) return false;
      if (skier.skill === 'intermediate' && t.difficulty === 'double_black') return false;
      
      // Closed/snowless check
      if (t.snowDepth <= 0) return false;

      return true;
    });

    if (validTrails.length > 0) {
      // Pick a trail (weighted against recently skied ones to introduce variety)
      const unskiedTrails = validTrails.filter(t => !skier.recentTrails.includes(t.id));
      const pool = unskiedTrails.length > 0 ? unskiedTrails : validTrails;
      
      // Choose least crowded
      pool.sort((a, b) => a.skierDensities.length - b.skierDensities.length);
      const chosenTrail = pool[0];
      
      skier.state = 'skiing';
      skier.currentEdgeId = chosenTrail.id;
      skier.currentEdgeProgress = 0;
      chosenTrail.skierDensities.push({ skierId: skier.id, progress: 0 });
      return;
    }

    // 2. No outgoing trails. Look for a lift to ride up
    const validLifts = state.lifts.filter(l => l.sourceNodeId === skier.currentNodeId && !l.isClosed);
    if (validLifts.length > 0) {
      // Choose lift with shortest queue
      validLifts.sort((a, b) => a.queue.length - b.queue.length);
      const chosenLift = validLifts[0];
      
      skier.state = 'queuing';
      skier.currentEdgeId = chosenLift.id;
      chosenLift.queue.push(skier.id);
      return;
    }

    // 3. Stranded! No trails go down, no lifts go up.
    // Try to find any path down to exit
    const exitPath = findPath(skier.currentNodeId, n => n.type === 'entrance', state, skier.skill);
    if (exitPath && exitPath.length > 0) {
      skier.routeQueue = exitPath;
      this.followRoute(skier, state);
    } else {
      // Teleport home (fail-safe)
      skier.state = 'leaving';
    }
  }

  private static handleEndOfDay(state: GameState, alerts: string[]): void {
    // 1. Force clear all skiers
    state.skiers = [];
    
    // 2. Deduct operations expenses
    let dailyExpenses = 0;
    
    // Staff costs
    dailyExpenses += state.groomerCount * 150;
    dailyExpenses += state.patrolCount * 120;
    
    // Lift maintenance costs
    for (const lift of state.lifts) {
      dailyExpenses += lift.maintenanceCost;
    }

    // Trail maintenance costs
    for (const trail of state.trails) {
      dailyExpenses += trail.maintenanceCost;
    }

    state.ledger.expenses = dailyExpenses;
    state.ledger.net = state.ledger.revenue - state.ledger.expenses;
    
    // Apply net to cash balance
    state.cash += state.ledger.net;
    
    alerts.push(`🌙 Daily operations wrapped up! Expenses: -$${dailyExpenses}. Net Daily Balance: $${state.ledger.net >= 0 ? '+' : ''}${state.ledger.net}.`);

    // 3. Reset daily metrics
    state.ledger.revenue = 0;
    state.ledger.expenses = 0;
    state.ledger.net = 0;
    
    // 4. Advance to next day, reset clock to 8:00 AM
    state.gameDay++;
    if (state.gameDay > 30) {
      state.gameDay = 1;
      state.gameMonth = (state.gameMonth + 1) % 12;
    }
    state.gameTimeMinutes = 480; // 8:00 AM
    
    // 5. Apply Grooming/Snowmaking repairs overnight
    this.runOvernightMaintenance(state);
  }

  private static runOvernightMaintenance(state: GameState): void {
    // Groomers restore trail quality (each groomer repairs groomingQuality by 20% on up to 3 trails)
    const trailsNeedGrooming = [...state.trails].sort((a, b) => a.groomingQuality - b.groomingQuality);
    const groomerCapacity = state.groomerCount * 3;
    
    for (let i = 0; i < Math.min(groomerCapacity, trailsNeedGrooming.length); i++) {
      trailsNeedGrooming[i].groomingQuality = Math.min(1.0, trailsNeedGrooming[i].groomingQuality + 0.35);
    }

    // Active Snowmakers restore depth on their anchor nodes/edges
    for (const trail of state.trails) {
      // Natural compaction/settling overnight: slightly increase base
      trail.snowDepth = Math.max(0, trail.snowDepth - 0.5); // some compacting
      if (state.temperature < 30) {
        trail.snowDepth += 2.0; // slight freezing/frost
      }
    }
  }
}
