export default defineBackground(() => {
  browser.action.onClicked.addListener(async (tab) => {
    if (tab.windowId != null) {
      // @ts-expect-error -- sidePanel API types may lag behind Manifest V3
      await browser.sidePanel.open({ windowId: tab.windowId });
    }
  });
});
