import type { Role } from '../api/types'

/** Display titles, widest role first. */
export const ROLE_TITLES: Record<Role, string> = {
  SuperAdmin: 'System Administrator',
  GroupAdmin: 'Group Administrator',
  Owner: 'Restaurant Owner',
  Manager: 'Manager',
  TeamMember: 'Team Member',
}

/** One line on what each role can do, shown when picking a role. */
export const ROLE_HELP: Record<Role, string> = {
  SuperAdmin: 'Runs the whole system: every investment group, its administrators and settings.',
  GroupAdmin: 'Runs one investment group: every restaurant, every user account and the group settings.',
  Owner: 'Everything at the restaurants they own, including hiring, resetting and suspending the managers and team logins who work there.',
  Manager: 'Works at one or more restaurants with the permissions the owners grant. Sees roll-up reports across their restaurants.',
  TeamMember: 'A shared login for the crew. It can only enter guest cards.',
}

export const roleTitle = (r: Role) => ROLE_TITLES[r] ?? r
