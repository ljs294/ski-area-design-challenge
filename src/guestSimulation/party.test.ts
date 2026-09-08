import { describe, expect, it } from 'vitest';
import type { Party } from './contracts.ts';
import {
  assertPartyConservation,
  boardParty,
  createPartyAccounting,
  createPartyPlan,
  createPartyRendezvous,
  deserializePartyAccounting,
  exitPartyMembers,
  evaluatePartyPlanSafety,
  movePartyMembersToTrail,
  partyConservation,
  rollPartyMemberOutcomes,
  schedulePartyQueueBoarding,
  splitPartyAcrossConsecutiveChairs,
  serializePartyAccounting,
  type PartyMemberProfile,
} from './party.ts';

function makeParty(size: number, id = 'party-1'): { party: Party; members: PartyMemberProfile[] } {
  const guestIds = Array.from({ length: size }, (_, index) => `${id}-member-${index + 1}`);
  const members = guestIds.map((memberId, index) => ({ id: memberId, partyId: id, ordinal: index, ability: index === 1 ? 0.35 : 0.8, patience: 0.7, riskTolerance: 0.5 }));
  return {
    party: { id, guestIds, size, kind: 'friends', heavyGroup: size > 6, arrivalTick: 0, plannedDepartureTick: null, futurePartyId: null },
    members,
  };
}

describe('party foundation', () => {
  it('keeps the leader-owned shared plan safe for the weakest member', () => {
    const { party, members } = makeParty(3);
    const plan = createPartyPlan({
      party,
      members,
      worldSeed: 'party-test',
      routes: [
        { id: 'hard', liftId: 'lift-hard', trailId: 'trail-hard', minimumAbility: 0.7, leaderAppeal: 1 },
        { id: 'easy', liftId: 'lift-easy', trailId: 'trail-easy', minimumAbility: 0.3, leaderAppeal: 0.1 },
      ],
    });
    expect(plan.leaderId).toBe(members[0].id);
    expect(plan.ownership).toBe('leader-owned-shared-plan');
    expect(plan.weakestMemberId).toBe(members[1].id);
    expect(plan.selectedRouteId).toBe('easy');
    expect(evaluatePartyPlanSafety(plan, members).safe).toBe(true);
    expect(Object.prototype.hasOwnProperty.call(plan, 'groupMultiplier')).toBe(false);
  });

  it('splits an oversized party across consecutive chairs in member order', () => {
    const { party, members } = makeParty(5);
    const assignments = splitPartyAcrossConsecutiveChairs({
      partyId: party.id,
      memberIds: members.map((member) => member.id),
      chairs: [
        { id: 'chair-1', capacity: 2 },
        { id: 'chair-2', capacity: 2 },
        { id: 'chair-3', capacity: 2 },
      ],
    });
    expect(assignments.map((assignment) => assignment.chairId)).toEqual(['chair-1', 'chair-2', 'chair-3']);
    expect(assignments.map((assignment) => assignment.memberIds)).toEqual([
      [members[0].id, members[1].id],
      [members[2].id, members[3].id],
      [members[4].id],
    ]);
  });

  it('gives every waiting party a bounded fair turn, including oversized parties', () => {
    const large = makeParty(9, 'large');
    const small = makeParty(1, 'small');
    const result = schedulePartyQueueBoarding({
      queue: [
        { partyId: large.party.id, memberIds: large.members.map((member) => member.id), enqueuedTick: 0, queueOrder: 0 },
        { partyId: small.party.id, memberIds: small.members.map((member) => member.id), enqueuedTick: 0, queueOrder: 1 },
      ],
      chairs: Array.from({ length: 6 }, (_, index) => ({ id: `chair-${index + 1}`, capacity: 2 })),
    });
    expect(result.assignments).toHaveLength(6);
    expect(result.assignments[1].partyId).toBe(small.party.id);
    expect(result.queue[0].boardedMemberIds).toHaveLength(9);
    expect(result.queue[1].boardedMemberIds).toHaveLength(1);
    expect(result.maxPartyWaitRounds).toBeLessThanOrEqual(1);
  });

  it('conserves each member through queue, lift, trail, exit, and save-load', () => {
    const { party, members } = makeParty(7);
    let state = createPartyAccounting(members);
    expect(partyConservation(state)).toEqual({ total: 7, byLocation: { queue: 7, lift: 0, trail: 0, exit: 0 }, conserved: true });
    const assignments = splitPartyAcrossConsecutiveChairs({
      partyId: party.id,
      memberIds: members.map((member) => member.id),
      chairs: [{ id: 'chair-1', capacity: 4 }, { id: 'chair-2', capacity: 4 }],
    });
    state = boardParty(state, assignments, 5);
    expect(partyConservation(state).byLocation).toEqual({ queue: 0, lift: 7, trail: 0, exit: 0 });
    state = movePartyMembersToTrail(state, members.map((member) => member.id), 'trail-1');
    state = exitPartyMembers(state, members.map((member) => member.id), 120);
    assertPartyConservation(state, party.size);
    expect(partyConservation(state).byLocation).toEqual({ queue: 0, lift: 0, trail: 0, exit: 7 });
    const loaded = deserializePartyAccounting(serializePartyAccounting(state));
    expect(loaded).toEqual(state);
    expect(new Set(loaded.members.map((member) => member.identity.persistenceIdentity)).size).toBe(7);
  });

  it('rolls deterministic cohesion, injury, thought, and presentation identities per member', () => {
    const { party, members } = makeParty(4);
    const plan = createPartyPlan({ party, members, routes: [{ id: 'easy', minimumAbility: 0.3 }], worldSeed: 'outcome-seed' });
    const request = { party, members, plan, worldSeed: 'outcome-seed', tick: 30 } as const;
    const first = rollPartyMemberOutcomes(request);
    const second = rollPartyMemberOutcomes(request);
    expect(second).toEqual(first);
    expect(first).toHaveLength(members.length);
    expect(new Set(first.map((outcome) => outcome.identity.renderedDotId)).size).toBe(members.length);
    expect(new Set(first.map((outcome) => outcome.identity.seatIdentity)).size).toBe(members.length);
    expect(new Set(first.map((outcome) => outcome.identity.occupancyIdentity)).size).toBe(members.length);
    expect(new Set(first.map((outcome) => outcome.identity.thoughtIdentity)).size).toBe(members.length);
    expect(new Set(first.map((outcome) => outcome.identity.injuryRollIdentity)).size).toBe(members.length);
    expect(new Set(first.map((outcome) => outcome.identity.persistenceIdentity)).size).toBe(members.length);
    expect(first.every((outcome) => !Object.prototype.hasOwnProperty.call(outcome, 'groupMultiplier'))).toBe(true);
  });

  it('keeps all split members in the rendezvous token', () => {
    const { party, members } = makeParty(6);
    const rendezvous = createPartyRendezvous({ party, memberIds: members.map((member) => member.id), locationId: 'base-lodge', targetTick: 90 });
    expect(rendezvous.leaderId).toBe(members[0].id);
    expect(rendezvous.memberIds).toEqual(members.map((member) => member.id));
    expect(rendezvous.id).toBe('rendezvous:party-1:base-lodge:90');
  });
});
