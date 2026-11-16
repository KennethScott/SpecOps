// Drives the reconnect dialog in Components/Layout/ReconnectModal.razor (based on the Blazor template's script).
// Bundled to wwwroot/js/reconnect-modal.js by build-client.mjs.

function required<T extends HTMLElement>(id: string, type: new () => T): T {
  const element = document.getElementById(id);
  if (!(element instanceof type)) {
    throw new Error(`#${id} is missing from the page.`);
  }
  return element;
}

function blazor(): BlazorClient {
  if (!window.Blazor) {
    throw new Error('Blazor has not started.');
  }
  return window.Blazor;
}

const reconnectModal = required('components-reconnect-modal', HTMLDialogElement);

reconnectModal.addEventListener('components-reconnect-state-changed', (event) => {
  const { state } = (event as CustomEvent<ReconnectStateDetail>).detail;
  if (state === 'show') {
    reconnectModal.showModal();
  } else if (state === 'hide') {
    reconnectModal.close();
  } else if (state === 'failed') {
    document.addEventListener('visibilitychange', retryWhenDocumentBecomesVisible);
  } else if (state === 'rejected') {
    location.reload();
  }
});

required('components-reconnect-button', HTMLButtonElement).addEventListener('click', () => void retry());
required('components-resume-button', HTMLButtonElement).addEventListener('click', () => void resume());

async function retry(): Promise<void> {
  document.removeEventListener('visibilitychange', retryWhenDocumentBecomesVisible);

  try {
    // reconnect() resolves true on success, false if the server was reached but rejected the circuit (e.g. unknown
    // circuit ID), and throws if the server couldn't be reached.
    const successful = await blazor().reconnect();
    if (!successful) {
      // The circuit is gone. Try to resume it, else reload so the user can carry on as quickly as possible.
      const resumed = await blazor().resumeCircuit();
      if (resumed) {
        reconnectModal.close();
      } else {
        location.reload();
      }
    }
  } catch {
    // The server is unavailable; try again when the user comes back to the tab.
    document.addEventListener('visibilitychange', retryWhenDocumentBecomesVisible);
  }
}

async function resume(): Promise<void> {
  try {
    if (!(await blazor().resumeCircuit())) {
      location.reload();
    }
  } catch {
    reconnectModal.classList.replace('components-reconnect-paused', 'components-reconnect-resume-failed');
  }
}

function retryWhenDocumentBecomesVisible(): void {
  if (document.visibilityState === 'visible') {
    void retry();
  }
}
