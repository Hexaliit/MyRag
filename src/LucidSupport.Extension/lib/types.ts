/** A form field extracted from the page DOM. */
export interface ExtractedField {
  selector: string;
  type: string;
  name: string | null;
  label: string;
  placeholder: string | null;
  required: boolean;
  pattern: string | null;
  minLength: number | null;
  maxLength: number | null;
  autocomplete: string | null;
}

/** A navigation link detected on the page. */
export interface NavLink {
  role: string;
  label: string;
  selector: string;
}

/** Combined extraction result from a single page. */
export interface PageExtractionResult {
  url: string;
  title: string;
  fields: ExtractedField[];
  nav: Record<string, NavLink>;
}

// ── API DTOs (match AdminDtos.cs shape, camelCase) ──────────────────

export interface PageCreateRequest {
  pageId: string;
  title: string;
  urlPattern: string;
  description?: string;
  site?: string;
  fields?: AdminField[];
}

export interface AdminField {
  selector: string;
  label: string;
  type?: string;
  displayLabel?: string;
  placeholder?: string;
  pattern?: string;
  required: boolean;
  minLength?: number;
  maxLength?: number;
  autocomplete?: string;
  help?: string;
}

export interface PageDetail {
  pageId: string;
  urlPattern: string;
  title: string;
  description?: string;
  site?: string;
  fields: AdminField[];
}

export interface PageSummary {
  pageId: string;
  urlPattern: string;
  title: string;
  fieldCount: number;
}

export interface ApiError {
  error: string;
}
