import { describe, expect, it } from 'bun:test';

import { reviewQuestion } from '../src/review-question';

const URL = 'https://github.com/acme/shop/pull/12';

describe('reviewQuestion', () => {
  it('asks exactly the review question for a pull request link, as before, when not asked again', () => {
    expect(
      [reviewQuestion(URL), reviewQuestion(`${URL}#issuecomment-5`, false)],
      'the question every caller asks today is unchanged',
    ).toEqual([`Review the pull request ${URL}`, `Review the pull request ${URL}`]);
  });

  it('asked again, adds " again" to the same question and nothing else', () => {
    expect(reviewQuestion(URL, true), 'again changes the question only by its suffix').toBe(
      `Review the pull request ${URL} again`,
    );
  });

  it('asks nothing for text that is not a pull request link, again or not', () => {
    expect(
      [reviewQuestion('review everything', true), reviewQuestion(undefined, true)],
      'no link, no question',
    ).toEqual([null, null]);
  });
});
