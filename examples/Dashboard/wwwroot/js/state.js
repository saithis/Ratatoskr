/** @type {{ services: object | null, lastOrderId: string | null, inventoryDemoMode: string, requeueingIds: Set<string> }} */
export const state = {
  services: null,
  lastOrderId: null,
  inventoryDemoMode: 'off',
  requeueingIds: new Set(),
};
