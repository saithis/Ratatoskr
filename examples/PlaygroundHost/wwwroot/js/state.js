/** @type {{ services: object | null, lastOrderId: string | null, lastScenarioRunId: string | null, inventoryDemoMode: string, requeueingIds: Set<string> }} */
export const state = {
  services: null,
  lastOrderId: null,
  lastScenarioRunId: null,
  inventoryDemoMode: 'off',
  requeueingIds: new Set(),
};
