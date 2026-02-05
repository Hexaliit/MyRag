import type {
  PageCreateRequest,
  PageDetail,
  PageSummary,
  ApiError,
} from './types';

let baseUrl = 'http://localhost:5050';

export function setBaseUrl(url: string) {
  baseUrl = url.replace(/\/+$/, '');
}

export function getBaseUrl(): string {
  return baseUrl;
}

async function request<T>(
  path: string,
  options?: RequestInit,
): Promise<{ ok: true; data: T } | { ok: false; status: number; error: string }> {
  try {
    const res = await fetch(`${baseUrl}${path}`, {
      ...options,
      headers: {
        'Content-Type': 'application/json',
        ...options?.headers,
      },
    });

    if (!res.ok) {
      let error = `HTTP ${res.status}`;
      try {
        const body = (await res.json()) as ApiError;
        if (body.error) error = body.error;
      } catch {
        // Use status text
      }
      return { ok: false, status: res.status, error };
    }

    const data = (await res.json()) as T;
    return { ok: true, data };
  } catch (e) {
    return {
      ok: false,
      status: 0,
      error: e instanceof Error ? e.message : 'Network error',
    };
  }
}

export function createPage(dto: PageCreateRequest) {
  return request<PageDetail>('/api/admin/pages', {
    method: 'POST',
    body: JSON.stringify(dto),
  });
}

export function updatePage(pageId: string, dto: Partial<PageCreateRequest>) {
  return request<PageDetail>(`/api/admin/pages/${encodeURIComponent(pageId)}`, {
    method: 'PUT',
    body: JSON.stringify(dto),
  });
}

export function getPage(pageId: string) {
  return request<PageDetail>(
    `/api/admin/pages/${encodeURIComponent(pageId)}`,
  );
}

export function listPages() {
  return request<PageSummary[]>('/api/admin/pages');
}
