import { Pipe, type PipeTransform } from '@angular/core';

import { renderMarkdown } from './markdown';

/** `reply | markdown` for an `[innerHTML]` binding; Angular sanitises the result on the way in. */
@Pipe({ name: 'markdown', standalone: true, pure: true })
export class MarkdownPipe implements PipeTransform {
  transform(text: string | null | undefined): string {
    return renderMarkdown(text ?? '');
  }
}
